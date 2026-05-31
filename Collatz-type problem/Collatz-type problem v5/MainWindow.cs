using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;

namespace BinaryRewrite
{
    public sealed class MainWindow : Window
    {
        internal static readonly FontFamily MonoFont =
            new FontFamily("Cascadia Code,Consolas,Menlo,DejaVu Sans Mono,monospace");
        internal static readonly Color[] Palette = AppTheme.Series;
        internal static readonly int CoreCount = Environment.ProcessorCount;

        private bool _cleanupDone, _cleanupInProgress;

        public MainWindow()
        {
            Title = "Binary-Rewrite Studio";
            Width = 1240; Height = 900;
            MinWidth = 960; MinHeight = 660;

            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(AppTheme.Bg0, 0),
                    new GradientStop(AppTheme.Bg1, 1)
                }
            };
            foreach (var st in BuildAppStyles()) Styles.Add(st);

            var tabs = new TabControl
            {
                Background = Brushes.Transparent,
                Margin = new Thickness(8, 0, 8, 8),
                Items =
                {
                    new TabItem { Header = "Orbit",       Content = BuildOrbitTab() },
                    new TabItem { Header = "Graphs",      Content = BuildGraphTab() },
                    new TabItem { Header = "Seed survey", Content = BuildSurveyTab() },
                    new TabItem { Header = "Conjugate T", Content = BuildConjugateTab() },
                }
            };

            var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            Grid.SetRow(tabs, 1);
            root.Children.Add(BuildHeader());
            root.Children.Add(tabs);
            Content = root;
        }

        private Control BuildHeader()
        {
            var dot = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(9),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(AppTheme.AccentHi, 0), new GradientStop(AppTheme.AccentLo, 1) }
                }
            };
            var title = new TextBlock
            {
                Text = "Binary-Rewrite Studio",
                FontSize = 18,
                FontWeight = FontWeight.Bold,
                Foreground = AppTheme.TextBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            var subtitle = new TextBlock
            {
                Text = "iterated binary-string rewriting · a Collatz-type halting study",
                FontSize = 12,
                Foreground = AppTheme.TextMutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Margin = new Thickness(20, 14, 20, 14),
                Children = { dot, title, subtitle }
            };
            return new Border
            {
                Child = bar,
                Background = new SolidColorBrush(Color.FromArgb(0x66, AppTheme.Surface.R, AppTheme.Surface.G, AppTheme.Surface.B)),
                BorderBrush = AppTheme.BorderBrush,
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
        }

        // ---------- app-wide control styling (modern dark) ----------
        private static Styles BuildAppStyles()
        {
            var styles = new Styles();

            Style S(Func<Selector, Selector> sel) { var st = new Style(sel); styles.Add(st); return st; }
            void Set(Style st, AvaloniaProperty p, object v) => st.Setters.Add(new Setter(p, v));

            // Buttons — accent, rounded, padded
            var btn = S(x => x.OfType<Button>());
            Set(btn, Button.BackgroundProperty, AppTheme.AccentBrush);
            Set(btn, Button.ForegroundProperty, Brushes.White);
            Set(btn, Button.CornerRadiusProperty, new CornerRadius(9));
            Set(btn, Button.PaddingProperty, new Thickness(15, 9));
            Set(btn, Button.BorderThicknessProperty, new Thickness(0));
            Set(btn, Button.FontSizeProperty, 13.0);
            Set(btn, Button.FontWeightProperty, FontWeight.Medium);
            Set(btn, Button.CursorProperty, new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand));

            var btnHover = S(x => x.OfType<Button>().Class(":pointerover").Template().OfType<ContentPresenter>());
            Set(btnHover, ContentPresenter.BackgroundProperty, AppTheme.AccentHiBrush);
            var btnPress = S(x => x.OfType<Button>().Class(":pressed").Template().OfType<ContentPresenter>());
            Set(btnPress, ContentPresenter.BackgroundProperty, AppTheme.AccentLoBrush);

            // TextBox — surface, rounded, subtle border
            var tb = S(x => x.OfType<TextBox>());
            Set(tb, TextBox.BackgroundProperty, AppTheme.SurfaceHiBrush);
            Set(tb, TextBox.ForegroundProperty, AppTheme.TextBrush);
            Set(tb, TextBox.BorderBrushProperty, AppTheme.BorderBrush);
            Set(tb, TextBox.BorderThicknessProperty, new Thickness(1));
            Set(tb, TextBox.CornerRadiusProperty, new CornerRadius(8));
            Set(tb, TextBox.PaddingProperty, new Thickness(10, 7));

            // ComboBox — surface, rounded
            var cb = S(x => x.OfType<ComboBox>());
            Set(cb, ComboBox.BackgroundProperty, AppTheme.SurfaceHiBrush);
            Set(cb, ComboBox.ForegroundProperty, AppTheme.TextBrush);
            Set(cb, ComboBox.BorderBrushProperty, AppTheme.BorderBrush);
            Set(cb, ComboBox.BorderThicknessProperty, new Thickness(1));
            Set(cb, ComboBox.CornerRadiusProperty, new CornerRadius(8));
            Set(cb, ComboBox.PaddingProperty, new Thickness(10, 7));

            // NumericUpDown — surface, rounded
            var nud = S(x => x.OfType<NumericUpDown>());
            Set(nud, NumericUpDown.BackgroundProperty, AppTheme.SurfaceHiBrush);
            Set(nud, NumericUpDown.ForegroundProperty, AppTheme.TextBrush);
            Set(nud, NumericUpDown.BorderBrushProperty, AppTheme.BorderBrush);
            Set(nud, NumericUpDown.BorderThicknessProperty, new Thickness(1));
            Set(nud, NumericUpDown.CornerRadiusProperty, new CornerRadius(8));

            // CheckBox + TextBlock text colour
            var chk = S(x => x.OfType<CheckBox>());
            Set(chk, CheckBox.ForegroundProperty, AppTheme.TextBrush);
            var tbl = S(x => x.OfType<TextBlock>());
            Set(tbl, TextBlock.ForegroundProperty, AppTheme.TextBrush);

            // ListBox — surface card
            var lb = S(x => x.OfType<ListBox>());
            Set(lb, ListBox.BackgroundProperty, new SolidColorBrush(AppTheme.ChartBg));
            Set(lb, ListBox.BorderBrushProperty, AppTheme.BorderBrush);
            Set(lb, ListBox.BorderThicknessProperty, new Thickness(1));
            Set(lb, ListBox.CornerRadiusProperty, new CornerRadius(10));
            Set(lb, ListBox.PaddingProperty, new Thickness(4));
            var lbi = S(x => x.OfType<ListBoxItem>());
            Set(lbi, ListBoxItem.ForegroundProperty, AppTheme.TextBrush);
            Set(lbi, ListBoxItem.PaddingProperty, new Thickness(8, 4));
            Set(lbi, ListBoxItem.CornerRadiusProperty, new CornerRadius(6));

            // ProgressBar — accent
            var pb = S(x => x.OfType<ProgressBar>());
            Set(pb, ProgressBar.ForegroundProperty, AppTheme.AccentBrush);
            Set(pb, ProgressBar.BackgroundProperty, AppTheme.SurfaceHiBrush);
            Set(pb, ProgressBar.CornerRadiusProperty, new CornerRadius(8));
            Set(pb, ProgressBar.MinHeightProperty, 8.0);

            // TabItem — modern
            var ti = S(x => x.OfType<TabItem>());
            Set(ti, TabItem.FontSizeProperty, 14.0);
            Set(ti, TabItem.ForegroundProperty, AppTheme.TextMutedBrush);
            Set(ti, TabItem.PaddingProperty, new Thickness(16, 9));
            Set(ti, TabItem.MarginProperty, new Thickness(2, 0));
            var tiSel = S(x => x.OfType<TabItem>().Class(":selected"));
            Set(tiSel, TabItem.ForegroundProperty, AppTheme.TextBrush);

            return styles;
        }

        // ---------- helpers ----------
        private static TextBlock Lbl(string t) => new TextBlock
        { Text = t, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };

        private static Control Section(string t)
        {
            var bar = new Border
            {
                Width = 4,
                Height = 18,
                CornerRadius = new CornerRadius(2),
                Background = AppTheme.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            var txt = new TextBlock
            {
                Text = t,
                FontSize = 14.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = AppTheme.TextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 18, 0, 8),
                Children = { bar, txt }
            };
        }

        // a rounded "card" container
        private static Border Card(Control content, Thickness? margin = null) => new Border
        {
            Child = content,
            Background = AppTheme.SurfaceBrush,
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
            Margin = margin ?? new Thickness(0, 0, 0, 4),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetX = 0, OffsetY = 6, Blur = 22, Spread = 0, Color = Color.FromArgb(0x45, 0, 0, 0) })
        };

        // a chart container (rounded, transparent so the chart's own panel shows)
        private static Border ChartBox(Control chart, double? height = null)
        {
            var b = new Border
            {
                Child = chart,
                BorderBrush = AppTheme.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(0, 2, 0, 2),
                ClipToBounds = true
            };
            if (height.HasValue) b.Height = height.Value;
            return b;
        }

        // ---- PNG export of a chart ----
        private async Task SaveChartAsync(Window owner, Control chart, string suggestedName)
        {
            var top = TopLevel.GetTopLevel(owner);
            if (top?.StorageProvider == null) return;
            int w = (int)Math.Max(64, chart.Bounds.Width);
            int h = (int)Math.Max(64, chart.Bounds.Height);
            if (w < 64 || h < 64) return;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = suggestedName,
                DefaultExtension = "png",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("PNG image") { Patterns = new[] { "*.png" } },
                    new FilePickerFileType("All files") { Patterns = new[] { "*.*" } }
                }
            });
            if (file == null) return;
            var pixelSize = new Avalonia.PixelSize(w, h);
            using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(pixelSize, new Avalonia.Vector(96, 96));
            rtb.Render(chart);
            using var stream = await file.OpenWriteAsync();
            rtb.Save(stream);
        }

        private Button SaveGraphButton(Window owner, Control chart, Func<string> nameProvider)
        {
            var b = new Button { Content = "Save graph (PNG)" };
            b.Click += async (_, __) => { try { await SaveChartAsync(owner, chart, nameProvider()); } catch { } };
            return b;
        }

        private static StackPanel Row(params Control[] kids)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 4) };
            foreach (var k in kids) p.Children.Add(k);
            return p;
        }

        private static NumericUpDown Num(decimal val, decimal min, decimal max, decimal inc = 1, double w = 160)
            => new NumericUpDown
            {
                Value = val,
                Minimum = min,
                Maximum = max,
                Increment = inc,
                Width = w,
                FormatString = "0",
                FontSize = 14,
                MinHeight = 34
            };

        private static TextBox Tb(string t, double w, string watermark = null)
            => new TextBox { Text = t, Width = w, Watermark = watermark, FontSize = 14, MinHeight = 34 };

        private static ComboBox Combo(double w, IEnumerable<string> items, int selected = 0)
            => new ComboBox { Width = w, ItemsSource = items, SelectedIndex = selected, FontSize = 14, MinHeight = 34 };

        private static SelectableTextBlock Mono()
            => new SelectableTextBlock { FontFamily = MonoFont, FontSize = 12.5, TextWrapping = TextWrapping.NoWrap };

        private static ProgressBar Bar() => new ProgressBar { Minimum = 0, Maximum = 1, Height = 16, Width = 320 };

        private static int IntOf(NumericUpDown n) => (int)(n.Value ?? 0);
        private static long LongOf(NumericUpDown n) => (long)(n.Value ?? 0);

        private static readonly string[] EngineList4 =
            { "Auto (memory → disk)", "Bit (small, exact)", "Gap (in-memory)", "Gap (disk stream)" };
        private static EngineKind EngineFrom4(int idx) => idx switch
        {
            1 => EngineKind.Bit,
            2 => EngineKind.GapMemory,
            3 => EngineKind.GapDisk,
            _ => EngineKind.Auto
        };

        // ---- export helpers ----
        private async Task SaveTextAsync(Window owner, string content, string suggestedName)
        {
            var top = TopLevel.GetTopLevel(owner);
            if (top?.StorageProvider == null) return;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = suggestedName,
                DefaultExtension = "tsv",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Tab-separated values") { Patterns = new[] { "*.tsv" } },
                    new FilePickerFileType("Comma-separated values") { Patterns = new[] { "*.csv" } },
                    new FilePickerFileType("Text file") { Patterns = new[] { "*.txt" } },
                    new FilePickerFileType("All files") { Patterns = new[] { "*.*" } }
                }
            });
            if (file == null) return;
            using var s = await file.OpenWriteAsync();
            using var w = new StreamWriter(s);
            await w.WriteAsync(content);
        }

        private async Task ClipboardWriteAsync(Window owner, string text)
        {
            var top = TopLevel.GetTopLevel(owner);
            if (top?.Clipboard == null) return;
            await top.Clipboard.SetTextAsync(text);
        }

        private Button ExportFileButton(Func<string> producer, Func<string> nameProvider)
        {
            var b = new Button { Content = "Save to file…" };
            b.Click += async (_, __) => { try { await SaveTextAsync(this, producer(), nameProvider()); } catch { } };
            return b;
        }

        private Button ClipboardButton(Func<string> producer)
        {
            var b = new Button { Content = "Copy" };
            b.Click += async (_, __) => { try { await ClipboardWriteAsync(this, producer()); } catch { } };
            return b;
        }

        // ================================================================ ORBIT
        private TextBox _orbSeed;
        private NumericUpDown _orbSteps, _orbCores;
        private ComboBox _orbEngine;
        private CheckBox _orbOmega, _orbValue, _orbSpill;
        private SelectableTextBlock _orbTable, _orbStrings, _orbDec;
        private NumericUpDown _orbDecN;
        private TextBlock _orbStatus;
        private ProgressBar _orbBar;
        private CancellationTokenSource _orbCts;
        private List<StepInfo> _orbLastRows;

        private Control BuildOrbitTab()
        {
            _orbSeed = Tb("10", 280, "binary seed L0");
            _orbSteps = Num(26, 0, 1_000_000, 1);
            _orbCores = Num(CoreCount, 1, CoreCount, 1, 130);
            _orbEngine = Combo(220, EngineList4, 0);
            _orbOmega = new CheckBox { Content = "omega", IsChecked = true };
            _orbValue = new CheckBox { Content = "value V (base 10)", IsChecked = true };
            _orbSpill = new CheckBox { Content = "auto-spill to disk", IsChecked = true };
            var run = new Button { Content = "Run orbit" };
            run.Click += async (_, __) => await RunOrbit();
            var cancel = new Button { Content = "Cancel" };
            cancel.Click += (_, __) => _orbCts?.Cancel();
            var pop = new Button { Content = "Open in new window" };
            pop.Click += (_, __) => { if (!string.IsNullOrWhiteSpace(_orbSeed.Text)) OpenSeedDetail(_orbSeed.Text.Trim()); };
            _orbStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.WarningBrush };
            _orbBar = Bar();

            _orbTable = Mono();
            _orbStrings = Mono();
            _orbDecN = Num(10, 0, 1_000_000, 1);
            var decBtn = new Button { Content = "Decompose s at n" };
            decBtn.Click += async (_, __) => await RunDecompose();
            _orbDec = Mono();

            var expFile = ExportFileButton(BuildOrbitExport, () => $"orbit_L{_orbSeed.Text?.Trim()}_n{IntOf(_orbSteps)}.tsv");
            var expClip = ClipboardButton(BuildOrbitExport);

            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(Row(Lbl("Seed L0"), _orbSeed, Lbl("max steps"), _orbSteps, Lbl($"cores (of {CoreCount})"), _orbCores));
            panel.Children.Add(Row(Lbl("engine"), _orbEngine, _orbSpill, _orbOmega, _orbValue));
            panel.Children.Add(Row(run, cancel, pop, expFile, expClip));
            panel.Children.Add(Row(_orbBar, _orbStatus));
            panel.Children.Add(new TextBlock { Text = "n  s_n  par(nu)  nu(L_n)  omega  |L_n|  V(L_n)", FontFamily = MonoFont, Foreground = AppTheme.TextMutedBrush, Margin = new Thickness(0, 6, 0, 0) });
            panel.Children.Add(new ScrollViewer { Height = 260, Content = _orbTable, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
            panel.Children.Add(Row(Lbl("decompose at n ="), _orbDecN, decBtn));
            panel.Children.Add(_orbDec);
            panel.Children.Add(new TextBlock { Text = "strings L_n (only where |L_n| is small):", Foreground = AppTheme.TextMutedBrush, Margin = new Thickness(0, 6, 0, 0) });
            panel.Children.Add(new ScrollViewer { Height = 110, Content = _orbStrings, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
            panel.Children.Add(new TextBlock
            {
                Foreground = AppTheme.TextMutedBrush,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Text = "Cores accelerate the per-step gap-vector construction when ν is large. " +
                       "Auto-spill hands the state off to disk if the in-memory vector would exceed its cap. " +
                       "The decompose button uses these same settings."
            });

            return new ScrollViewer { Content = Card(panel, new Thickness(12)) };
        }

        private string BuildOrbitExport()
        {
            if (_orbLastRows == null) return "(no orbit yet)";
            var sb = new StringBuilder();
            sb.AppendLine($"# seed L0 = {(_orbSeed.Text ?? "").Trim()}");
            sb.AppendLine("n\ts_n\tparity\tnu\tomega\tlength\tV");
            foreach (var r in _orbLastRows)
            {
                string v = r.HasValue ? r.Value.ToString(CultureInfo.InvariantCulture)
                                      : "~" + Value.DecimalDigits(r.Length) + "d";
                sb.Append(r.N).Append('\t').Append(r.S).Append('\t')
                  .Append(r.ParityChar).Append('\t').Append(r.Nu).Append('\t')
                  .Append(r.Omega < 0 ? "-" : r.Omega.ToString()).Append('\t')
                  .Append(r.Length).Append('\t').Append(v).Append('\n');
            }
            return sb.ToString();
        }

        private async Task RunOrbit()
        {
            _orbCts?.Cancel();
            _orbCts = new CancellationTokenSource();
            var ct = _orbCts.Token;
            string seed = (_orbSeed.Text ?? "").Trim();
            int steps = IntOf(_orbSteps);
            int cores = Math.Max(1, Math.Min(CoreCount, IntOf(_orbCores)));
            var eng = EngineFrom4(_orbEngine.SelectedIndex);
            bool wantOmega = _orbOmega.IsChecked == true;
            bool wantVal = _orbValue.IsChecked == true;
            bool spill = _orbSpill.IsChecked == true;
            _orbStatus.Text = "running...";
            _orbBar.Maximum = Math.Max(1, steps);
            _orbBar.Value = 0;
            var prog = new Progress<int>(n => _orbBar.Value = n);

            try
            {
                TrajectoryRunner runner = null;
                var rows = await Task.Run(() =>
                {
                    runner = new TrajectoryRunner
                    {
                        Seed = seed,
                        MaxSteps = steps,
                        Engine = eng,
                        ComputeOmega = wantOmega,
                        ComputeValue = wantVal,
                        AllowDiskFallback = spill,
                        Cores = cores
                    };
                    return runner.Run(ct, prog);
                }, ct);

                _orbLastRows = rows;
                var sb = new StringBuilder();
                var strs = new StringBuilder();
                foreach (var r in rows)
                {
                    string v = !wantVal ? "-" :
                        r.HasValue ? FormatBig(r.Value) : "~" + Value.DecimalDigits(r.Length) + " digits";
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
                string tail = "";
                if (rows.Count > 0 && rows[rows.Count - 1].Halted) tail += " (halted)";
                if (runner != null && runner.SpilledToDisk) tail += $" [spilled to disk at n={runner.SpillStep}]";
                _orbStatus.Text = $"done: {rows.Count} steps" + tail;
            }
            catch (OperationCanceledException) { _orbStatus.Text = "cancelled"; }
            catch (Exception ex) { _orbStatus.Text = "error: " + ex.Message; }
        }

        private async Task RunDecompose()
        {
            string seed = (_orbSeed.Text ?? "").Trim();
            int n = IntOf(_orbDecN);
            int cores = Math.Max(1, Math.Min(CoreCount, IntOf(_orbCores)));
            bool spill = _orbSpill.IsChecked == true;
            try
            {
                var d = await Task.Run(() => new TrajectoryRunner
                {
                    Seed = seed,
                    Cores = cores,
                    AllowDiskFallback = spill
                }.DecomposeAt(n, CancellationToken.None));
                if (d == null) { _orbDec.Text = "no decomposition (halted before n or nu=0)."; return; }
                var sb = new StringBuilder();
                sb.Append($"s_{d.N} = {d.S}    contributing gaps = {d.Count}    size surplus = {d.Surplus}\n");
                sb.Append("multiset {gap: count}:  ");
                foreach (var kv in d.Multiset) sb.Append($"{kv.Key}:{kv.Value}  ");
                _orbDec.Text = sb.ToString();
            }
            catch (Exception ex) { _orbDec.Text = "error: " + ex.Message; }
        }

        // ================================================================ GRAPHS
        private TextBox _grSeeds;
        private NumericUpDown _grSteps, _grCores;
        private ComboBox _grMetric, _grEngine;
        private CheckBox _grLog, _grSpill;
        private ChartControl _chart;
        private TextBlock _grStatus;
        private ProgressBar _grBar;
        private CancellationTokenSource _grCts;
        private List<(string seed, List<StepInfo> rows)> _grLastData;

        private Control BuildGraphTab()
        {
            _grSeeds = Tb("10, 1011, 110", 340, "seeds, comma-separated");
            _grSteps = Num(20, 0, 1_000_000, 1);
            _grCores = Num(CoreCount, 1, CoreCount, 1, 130);
            _grMetric = Combo(220, new[] { "s_n (counter)", "nu(L_n)", "|L_n| length", "log10 V(L_n)", "omega(L_n)" }, 0);
            _grEngine = Combo(220, EngineList4, 0);
            _grLog = new CheckBox { Content = "log Y", IsChecked = false };
            _grSpill = new CheckBox { Content = "auto-spill", IsChecked = true };
            var plot = new Button { Content = "Plot" };
            plot.Click += async (_, __) => await RunPlot();
            var cancel = new Button { Content = "Cancel" };
            cancel.Click += (_, __) => _grCts?.Cancel();
            var expFile = ExportFileButton(BuildGraphsExport, () => "graphs.tsv");
            var expClip = ClipboardButton(BuildGraphsExport);
            var saveImg = SaveGraphButton(this, _chart = new ChartControl { MinHeight = 360 }, () => "graph.png");
            _grStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.WarningBrush };
            _grBar = Bar();

            var controls = new StackPanel { Spacing = 4 };
            controls.Children.Add(Row(Lbl("Seeds"), _grSeeds, Lbl("steps"), _grSteps, Lbl($"cores (of {CoreCount})"), _grCores));
            controls.Children.Add(Row(Lbl("engine"), _grEngine, _grSpill, Lbl("metric"), _grMetric, _grLog));
            controls.Children.Add(Row(plot, cancel, expFile, expClip, saveImg));
            controls.Children.Add(Row(_grBar, _grStatus));

            var chartBorder = ChartBox(_chart);
            chartBorder.Margin = new Thickness(12, 0, 12, 12);
            var controlsCard = Card(controls, new Thickness(12, 12, 12, 4));
            var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            Grid.SetRow(controlsCard, 0); Grid.SetRow(chartBorder, 1);
            grid.Children.Add(controlsCard); grid.Children.Add(chartBorder);
            return grid;
        }

        private string BuildGraphsExport()
        {
            if (_grLastData == null) return "(no plot yet)";
            var sb = new StringBuilder();
            sb.AppendLine($"# metric: {_grMetric.SelectedItem}");
            foreach (var (seed, rows) in _grLastData)
            {
                sb.AppendLine($"# seed L0 = {seed}");
                sb.AppendLine("n\ts_n\tnu\tomega\tlength\tV");
                foreach (var r in rows)
                {
                    string v = r.HasValue ? r.Value.ToString(CultureInfo.InvariantCulture)
                                          : "~" + Value.DecimalDigits(r.Length) + "d";
                    sb.Append(r.N).Append('\t').Append(r.S).Append('\t').Append(r.Nu).Append('\t')
                      .Append(r.Omega < 0 ? "-" : r.Omega.ToString()).Append('\t')
                      .Append(r.Length).Append('\t').Append(v).Append('\n');
                }
                sb.AppendLine();
            }
            return sb.ToString();
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
            int cores = Math.Max(1, Math.Min(CoreCount, IntOf(_grCores)));
            var eng = EngineFrom4(_grEngine.SelectedIndex);
            bool spill = _grSpill.IsChecked == true;
            _grStatus.Text = "computing...";
            _grBar.Value = 0;
            var prog = new Progress<double>(v => _grBar.Value = v);

            try
            {
                var (series, dataPerSeed) = await Task.Run(() =>
                {
                    var arr = new ChartSeries[seeds.Count];
                    var dataArr = new (string, List<StepInfo>)[seeds.Count];
                    int doneCount = 0;
                    Parallel.For(0, seeds.Count,
                        new ParallelOptions { MaxDegreeOfParallelism = cores, CancellationToken = ct },
                        i =>
                        {
                            var runner = new TrajectoryRunner
                            {
                                Seed = seeds[i],
                                MaxSteps = steps,
                                Engine = eng,
                                ComputeOmega = metric == 4,
                                ComputeValue = metric == 3,
                                AllowDiskFallback = spill
                            };
                            var rows = runner.Run(ct);
                            var cs = new ChartSeries { Name = "L0=" + seeds[i], Color = Palette[i % Palette.Length] };
                            foreach (var r in rows) cs.Values.Add(MetricValue(metric, r));
                            arr[i] = cs;
                            dataArr[i] = (seeds[i], rows);
                            int d = Interlocked.Increment(ref doneCount);
                            ((IProgress<double>)prog).Report((double)d / Math.Max(1, seeds.Count));
                        });
                    return (arr.Where(x => x != null).ToList(), dataArr.Where(x => x.Item2 != null).ToList());
                }, ct);

                _grLastData = dataPerSeed;
                _chart.Series.Clear();
                _chart.Series.AddRange(series);
                _chart.ShowLegend = true;
                _chart.LogY = _grLog.IsChecked == true;
                _chart.Title = _grMetric.SelectedItem?.ToString() ?? "";
                _chart.YLabel = _chart.Title;
                _chart.XOffset = 0; _chart.XLabel = "step n";
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

        // ================================================================ SURVEY
        private NumericUpDown _svFrom, _svTo, _svSteps, _svNu, _svCores;
        private CheckBox _svUnlimited, _svSpill;
        private ComboBox _svEngine, _svFilter;
        private NumericUpDown _svFilterLen;
        private ProgressBar _svBar;
        private SelectableTextBlock _svOut;
        private ListBox _svList;
        private TextBlock _svStatus, _svListHdr;
        private CancellationTokenSource _svCts;
        private SurveyResult _svResult;
        private List<SeedOutcome> _svAllSeeds = new List<SeedOutcome>();

        private CheckBox _svMaster, _svMgSpill;
        private ComboBox _svMgMetric;
        private NumericUpDown _svMgLen, _svMgCores;
        private ProgressBar _svMgBar;
        private TextBlock _svMgStatus;
        private ChartControl _svMgChart;
        private Border _svMgBorder;
        private CancellationTokenSource _svMgCts;

        private CheckBox _svHt;
        private ChartControl _svHtChart;
        private Border _svHtBorder;
        private TextBlock _svHtStatus;

        private CheckBox _svNh;
        private ChartControl _svNhChart;
        private Border _svNhBorder;

        private Control BuildSurveyTab()
        {
            _svFrom = Num(1, 1, 40, 1, 130);
            _svTo = Num(16, 1, 40, 1, 130);
            _svSteps = Num(400, 1, 1_000_000, 1, 160);
            _svNu = Num(3_000_000, 1000, 5_000_000_000, 100000, 200);
            _svCores = Num(CoreCount, 1, CoreCount, 1, 130);
            _svUnlimited = new CheckBox { Content = "no ν limit", IsChecked = false };
            _svUnlimited.IsCheckedChanged += (_, __) => _svNu.IsEnabled = _svUnlimited.IsChecked != true;
            _svSpill = new CheckBox { Content = "auto-spill per seed", IsChecked = false };
            _svEngine = Combo(220, new[] { "Gap (in-memory)", "Bit (small, exact)" }, 0);
            _svFilter = Combo(170, new[] { "All", "Halting", "Non-halting", "ν-cap", "Memory cap", "Step cap" }, 0);
            _svFilter.SelectionChanged += (_, __) => ApplySeedFilter();
            _svFilterLen = Num(0, 0, 40, 1, 100);
            _svFilterLen.ValueChanged += (_, __) => ApplySeedFilter();

            var run = new Button { Content = "Run survey" };
            run.Click += async (_, __) => await RunSurvey();
            var cancel = new Button { Content = "Cancel" };
            cancel.Click += (_, __) => _svCts?.Cancel();
            _svBar = Bar();
            _svStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.WarningBrush };
            _svOut = Mono();
            _svListHdr = new TextBlock { Foreground = AppTheme.TextMutedBrush };
            _svList = new ListBox { FontFamily = MonoFont, FontSize = 12.5, Height = 420 };
            _svList.SelectionChanged += (_, __) =>
            {
                if (_svList.SelectedItem is SeedOutcome so) { OpenSeedDetail(so.Seed); _svList.SelectedItem = null; }
            };
            var listExportFile = ExportFileButton(BuildSurveyExport, () => $"survey_len{IntOf(_svFrom)}-{IntOf(_svTo)}.tsv");
            var listExportClip = new Button { Content = "Copy seed list" };
            listExportClip.Click += async (_, __) =>
            {
                int total = _svAllSeeds.Count;
                if (total == 0) return;
                if (total > 50_000) { _svStatus.Text = "too many seeds to copy (50,000 max for clipboard)"; return; }
                try { await ClipboardWriteAsync(this, BuildSurveyExport()); _svStatus.Text = $"copied {total:#,0} seeds"; } catch { }
            };

            _svMaster = new CheckBox { Content = "enable", IsChecked = false };
            _svMaster.IsCheckedChanged += (_, __) => _svMgBorder.IsVisible = _svMaster.IsChecked == true;
            _svMgMetric = Combo(220, new[] { "s_n (counter)", "nu(L_n)", "|L_n| length", "log10 V(L_n)", "omega(L_n)" }, 0);
            _svMgLen = Num(0, 0, 40, 1, 100);
            _svMgCores = Num(CoreCount, 1, CoreCount, 1, 130);
            _svMgSpill = new CheckBox { Content = "auto-spill", IsChecked = false };
            var mgBuild = new Button { Content = "Build master graph" };
            mgBuild.Click += async (_, __) => await BuildMasterGraph();
            var mgCancel = new Button { Content = "Cancel" };
            mgCancel.Click += (_, __) => _svMgCts?.Cancel();
            _svMgBar = Bar();
            _svMgStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.WarningBrush };
            _svMgChart = new ChartControl { Height = 420, ShowLegend = false };
            _svMgBorder = ChartBox(_svMgChart);
            _svMgBorder.Margin = new Thickness(0, 4, 0, 0);
            _svMgBorder.IsVisible = false;
            var mgSave = SaveGraphButton(this, _svMgChart, () => "master_graph.png");

            _svHt = new CheckBox { Content = "enable", IsChecked = false };
            _svHt.IsCheckedChanged += (_, __) => _svHtBorder.IsVisible = _svHt.IsChecked == true;
            _svHtChart = new ChartControl { Height = 360, ShowLegend = false };
            _svHtBorder = ChartBox(_svHtChart);
            _svHtBorder.Margin = new Thickness(0, 4, 0, 0);
            _svHtBorder.IsVisible = false;
            var htBuild = new Button { Content = "Build halt-time scatter" };
            htBuild.Click += (_, __) => BuildHaltTimeChart();
            var htSave = SaveGraphButton(this, _svHtChart, () => "halt_time.png");
            _svHtStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.WarningBrush };

            _svNh = new CheckBox { Content = "enable", IsChecked = false };
            _svNh.IsCheckedChanged += (_, __) => _svNhBorder.IsVisible = _svNh.IsChecked == true;
            _svNhChart = new ChartControl { Height = 320 };
            _svNhBorder = ChartBox(_svNhChart);
            _svNhBorder.Margin = new Thickness(0, 4, 0, 0);
            _svNhBorder.IsVisible = false;
            var nhBuild = new Button { Content = "Build non-halting-by-length chart" };
            nhBuild.Click += (_, __) => BuildNonHaltingChart();
            var nhSave = SaveGraphButton(this, _svNhChart, () => "non_halting_by_length.png");

            var top = new StackPanel { Spacing = 4 };
            top.Children.Add(Row(Lbl("seed length from"), _svFrom, Lbl("to"), _svTo,
                                 Lbl("max steps"), _svSteps, Lbl($"cores (of {CoreCount})"), _svCores));
            top.Children.Add(Row(Lbl("max ν"), _svNu, _svUnlimited, Lbl("engine"), _svEngine, _svSpill, run, cancel));
            top.Children.Add(Row(_svBar, _svStatus));
            top.Children.Add(new TextBlock
            {
                Foreground = AppTheme.TextMutedBrush,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 4),
                Text = "Enumerates every seed (beginning with 1) in the length range across the chosen cores. The seed list " +
                       "always retains the first 100,000 seeds (smallest lengths first); larger surveys show a partial list. " +
                       "Auto-spill lets in-memory runaway seeds continue on disk; otherwise they're tagged 'memory cap'."
            });

            var root = new StackPanel { Margin = new Thickness(12), Spacing = 2 };
            root.Children.Add(Card(top));

            root.Children.Add(Section("Summary statistics"));
            root.Children.Add(Card(new ScrollViewer { Height = 220, Content = _svOut }));

            root.Children.Add(Section("Seed list"));
            root.Children.Add(Row(Lbl("status filter"), _svFilter, Lbl("length filter (0 = all)"), _svFilterLen));
            root.Children.Add(_svListHdr);
            root.Children.Add(_svList);
            root.Children.Add(Row(listExportFile, listExportClip));

            root.Children.Add(Section("Master graph (all/sampled trajectories)"));
            root.Children.Add(Row(_svMaster, Lbl("metric"), _svMgMetric, Lbl("length (0 = all)"), _svMgLen,
                                  Lbl($"cores (of {CoreCount})"), _svMgCores, _svMgSpill));
            root.Children.Add(Row(mgBuild, mgCancel, mgSave, _svMgBar, _svMgStatus));
            root.Children.Add(_svMgBorder);

            root.Children.Add(Section("Halting-time scatter"));
            root.Children.Add(new TextBlock
            {
                Foreground = AppTheme.TextMutedBrush,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Text = "X = seed index (sorted by length, then bit pattern). Y = halting step (0 if non-halting). " +
                       "Green = halts, red = does not."
            });
            root.Children.Add(Row(_svHt, htBuild, htSave, _svHtStatus));
            root.Children.Add(_svHtBorder);

            root.Children.Add(Section("Non-halting count by length"));
            root.Children.Add(Row(_svNh, nhBuild, nhSave));
            root.Children.Add(_svNhBorder);

            return new ScrollViewer
            {
                Content = root,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
        }

        private void ApplySeedFilter()
        {
            if (_svAllSeeds == null || _svAllSeeds.Count == 0) { _svList.ItemsSource = null; return; }
            int idx = _svFilter.SelectedIndex;
            int lenFilter = IntOf(_svFilterLen);
            IEnumerable<SeedOutcome> q = _svAllSeeds;
            switch (idx)
            {
                case 1: q = q.Where(o => o.Halted); break;
                case 2: q = q.Where(o => !o.Halted); break;
                case 3: q = q.Where(o => !o.Halted && o.Reason == NonHaltingReason.NuCap); break;
                case 4: q = q.Where(o => !o.Halted && o.Reason == NonHaltingReason.ResourceLimit); break;
                case 5: q = q.Where(o => !o.Halted && o.Reason == NonHaltingReason.StepCap); break;
            }
            if (lenFilter > 0) q = q.Where(o => o.Length == lenFilter);
            var list = q.ToList();
            _svList.ItemsSource = list;
            _svListHdr.Text = _svResult != null && !_svResult.SeedsComplete
                ? $"{list.Count:#,0} shown / {_svAllSeeds.Count:#,0} retained / {_svResult.TotalSeeds:#,0} total — click for trajectory"
                : $"{list.Count:#,0} of {_svAllSeeds.Count:#,0} seeds shown — click any seed for its full trajectory";
        }

        private string BuildSurveyExport()
        {
            var sb = new StringBuilder();
            if (_svResult == null) { sb.AppendLine("# (no survey yet)"); return sb.ToString(); }
            foreach (var line in FormatSurvey(_svResult).Split('\n')) sb.Append("# ").Append(line).Append('\n');
            sb.AppendLine();
            sb.AppendLine("seed\tlength\thalted\thalt_step\thalt_counter\treason");
            foreach (var o in _svAllSeeds)
            {
                sb.Append(o.Seed).Append('\t').Append(o.Length).Append('\t')
                  .Append(o.Halted ? "yes" : "no").Append('\t')
                  .Append(o.Halted ? o.HaltStep.ToString() : "-").Append('\t')
                  .Append(o.Halted ? o.HaltCounter.ToString() : "-").Append('\t')
                  .Append(o.Halted ? "halt" : o.Reason.ToString()).Append('\n');
            }
            return sb.ToString();
        }

        private async Task RunSurvey()
        {
            _svCts?.Cancel(); _svCts = new CancellationTokenSource();
            var ct = _svCts.Token;
            int from = IntOf(_svFrom), to = IntOf(_svTo), steps = IntOf(_svSteps);
            int cores = Math.Max(1, Math.Min(CoreCount, IntOf(_svCores)));
            long maxNu = _svUnlimited.IsChecked == true ? long.MaxValue : LongOf(_svNu);
            var eng = _svEngine.SelectedIndex == 1 ? EngineKind.Bit : EngineKind.GapMemory;
            bool spill = _svSpill.IsChecked == true;
            if (to < from) { _svStatus.Text = "range invalid"; return; }
            _svStatus.Text = $"running on {cores} core(s)...";
            _svBar.Value = 0;
            _svAllSeeds = new List<SeedOutcome>();
            _svResult = null;
            _svList.ItemsSource = null;
            _svListHdr.Text = "";
            var prog = new Progress<double>(v => _svBar.Value = v);
            try
            {
                var res = await Task.Run(() => SeedSurvey.Run(from, to, steps, maxNu, cores, 100_000, eng, spill, ct, prog), ct);
                _svBar.Value = 1;
                _svResult = res;
                _svAllSeeds = res.Seeds;
                _svOut.Text = FormatSurvey(res);
                ApplySeedFilter();
                _svStatus.Text = $"done in {res.ElapsedSeconds:0.00}s";
            }
            catch (OperationCanceledException) { _svStatus.Text = "cancelled"; }
            catch (Exception ex) { _svStatus.Text = "error: " + ex.Message; }
        }

        private async Task BuildMasterGraph()
        {
            if (_svAllSeeds == null || _svAllSeeds.Count == 0) { _svMgStatus.Text = "no seeds collected yet"; return; }
            if (_svMaster.IsChecked != true) { _svMaster.IsChecked = true; _svMgBorder.IsVisible = true; }
            _svMgCts?.Cancel(); _svMgCts = new CancellationTokenSource();
            var ct = _svMgCts.Token;
            int steps = Math.Min(IntOf(_svSteps), 200);
            int cores = Math.Max(1, Math.Min(CoreCount, IntOf(_svMgCores)));
            int lenFilter = IntOf(_svMgLen);
            int metric = _svMgMetric.SelectedIndex;
            bool spill = _svMgSpill.IsChecked == true;

            var seeds = lenFilter > 0
                ? _svAllSeeds.Where(o => o.Length == lenFilter).ToList()
                : _svAllSeeds.ToList();
            if (seeds.Count == 0) { _svMgStatus.Text = "no seeds match the length filter"; return; }

            const int MaxPlot = 500;
            List<SeedOutcome> sample;
            if (seeds.Count <= MaxPlot) sample = seeds;
            else
            {
                sample = new List<SeedOutcome>(MaxPlot);
                double stride = (double)seeds.Count / MaxPlot;
                for (int k = 0; k < MaxPlot; k++) sample.Add(seeds[(int)(k * stride)]);
            }

            _svMgStatus.Text = $"computing {sample.Count:#,0} trajectories...";
            _svMgBar.Value = 0;
            var prog = new Progress<double>(v => _svMgBar.Value = v);
            var haltColor = Color.FromArgb(0x60, 0x7b, 0xed, 0x9f);
            var nonColor = Color.FromArgb(0x60, 0xff, 0x6b, 0x6b);

            try
            {
                var series = await Task.Run(() =>
                {
                    var arr = new ChartSeries[sample.Count];
                    int done = 0;
                    Parallel.For(0, sample.Count,
                        new ParallelOptions { MaxDegreeOfParallelism = cores, CancellationToken = ct },
                        i =>
                        {
                            var so = sample[i];
                            var cs = new ChartSeries
                            {
                                Name = so.Seed,
                                Color = so.Halted ? haltColor : nonColor,
                                ShowMarkers = false,
                                Thickness = 1.0
                            };
                            try
                            {
                                var runner = new TrajectoryRunner
                                {
                                    Seed = so.Seed,
                                    MaxSteps = steps,
                                    Engine = EngineKind.GapMemory,
                                    AllowDiskFallback = spill,
                                    ComputeOmega = metric == 4,
                                    ComputeValue = metric == 3
                                };
                                var rows = runner.Run(ct);
                                foreach (var r in rows) cs.Values.Add(MetricValue(metric, r));
                            }
                            catch { /* truncated series; fine */ }
                            arr[i] = cs;
                            int d = Interlocked.Increment(ref done);
                            ((IProgress<double>)prog).Report((double)d / Math.Max(1, sample.Count));
                        });
                    return arr.Where(x => x != null && x.Values.Count > 0).ToList();
                }, ct);

                _svMgChart.Series.Clear();
                _svMgChart.Series.AddRange(series);
                _svMgChart.ShowLegend = false;
                _svMgChart.LogY = false;
                _svMgChart.XOffset = 0; _svMgChart.XLabel = "step n";
                _svMgChart.Title = $"master graph — {_svMgMetric.SelectedItem}"
                                 + (lenFilter > 0 ? $" — length {lenFilter} only" : "")
                                 + "    (green = halts, red = non-halting)";
                _svMgChart.YLabel = _svMgMetric.SelectedItem?.ToString() ?? "";
                _svMgChart.Refresh();
                _svMgBar.Value = 1;
                _svMgStatus.Text = $"drew {series.Count:#,0} trajectories"
                    + (seeds.Count > MaxPlot ? $" (sampled from {seeds.Count:#,0})" : "");
            }
            catch (OperationCanceledException) { _svMgStatus.Text = "cancelled"; }
            catch (Exception ex) { _svMgStatus.Text = "error: " + ex.Message; }
        }

        private void BuildHaltTimeChart()
        {
            if (_svAllSeeds == null || _svAllSeeds.Count == 0) { _svHtStatus.Text = "no seeds"; return; }
            if (_svHt.IsChecked != true) { _svHt.IsChecked = true; _svHtBorder.IsVisible = true; }

            int n = _svAllSeeds.Count;
            var halt = new ChartSeries
            {
                Name = "halting",
                Color = Color.FromRgb(0x7b, 0xed, 0x9f),
                DrawLines = false,
                MarkerRadius = 1.6
            };
            var nonh = new ChartSeries
            {
                Name = "non-halting",
                Color = Color.FromRgb(0xff, 0x6b, 0x6b),
                DrawLines = false,
                MarkerRadius = 1.6
            };
            for (int i = 0; i < n; i++)
            {
                var o = _svAllSeeds[i];
                if (o.Halted) { halt.Values.Add(o.HaltStep); nonh.Values.Add(double.NaN); }
                else { halt.Values.Add(double.NaN); nonh.Values.Add(0); }
            }
            _svHtChart.Series.Clear();
            _svHtChart.Series.Add(halt);
            _svHtChart.Series.Add(nonh);
            _svHtChart.LogY = false;
            _svHtChart.ShowLegend = true;
            _svHtChart.XOffset = 0; _svHtChart.XLabel = "seed index";
            _svHtChart.Title = $"halting time per seed ({n:#,0} seeds)";
            _svHtChart.YLabel = "halt step (0 if non-halting)";
            _svHtChart.Refresh();
            _svHtStatus.Text = $"plotted {n:#,0} seeds";
        }

        private void BuildNonHaltingChart()
        {
            if (_svResult == null) return;
            if (_svNh.IsChecked != true) { _svNh.IsChecked = true; _svNhBorder.IsVisible = true; }
            var s = new ChartSeries
            {
                Name = "non-halting",
                Color = Color.FromRgb(0xff, 0x9f, 0x43),
                Thickness = 2.2
            };
            for (int len = _svResult.LengthFrom; len <= _svResult.LengthTo; len++)
                s.Values.Add(_svResult.NonHaltingOfLength(len));
            _svNhChart.Series.Clear();
            _svNhChart.Series.Add(s);
            _svNhChart.LogY = false;
            _svNhChart.ShowLegend = false;
            _svNhChart.XOffset = _svResult.LengthFrom;
            _svNhChart.XLabel = "seed length";
            _svNhChart.YLabel = "# non-halting seeds";
            _svNhChart.Title = $"non-halting seed count by length (lengths {_svResult.LengthFrom}..{_svResult.LengthTo})";
            _svNhChart.Refresh();
        }

        private static string FormatSurvey(SurveyResult r)
        {
            var sb = new StringBuilder();
            sb.Append($"seed lengths {r.LengthFrom}..{r.LengthTo}\n");
            sb.Append($"total seeds          : {r.TotalSeeds:#,0}\n");
            sb.Append($"halting              : {r.Halting:#,0}  ({r.HaltFraction:P2})\n");
            sb.Append($"non-halting          : {r.NonHalting:#,0}\n");
            sb.Append($"  → ν cap hit        : {r.NuCapped:#,0}\n");
            sb.Append($"  → memory limit hit : {r.ResourceCapped:#,0}\n");
            sb.Append($"  → step cap hit     : {r.StepCapped:#,0}\n");
            sb.Append($"seeds retained in list: {r.TotalCollected:#,0}  ({(r.SeedsComplete ? "complete" : "partial — smallest lengths first")})\n\n");
            sb.Append($"halt at step 0 / 1   : {r.HaltStep0:#,0} / {r.HaltStep1:#,0}\n");
            sb.Append($"max halting step     : {r.MaxHaltStep}\n");
            sb.Append($"counter at halt 0/1  : {r.HaltCounter0:#,0} / {r.HaltCounter1:#,0}\n");
            sb.Append($"first halts (n>=2)   : {r.FirstHaltN2Plus:#,0}\n");
            sb.Append($"  violating s(N-2)<=5: {r.FirstHaltViolatingBound:#,0}   (expect 0)\n");
            sb.Append($"grazers g(L0)>=1     : {r.Grazers:#,0}   (max multiplicity {r.MaxGrazeMultiplicity})\n");
            sb.Append($"2-step-monotonicity violators: {r.MonotonicityViolators:#,0}\n\n");
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

        // ================================================================ seed-detail popup (independent window)
        private void OpenSeedDetail(string seed)
        {
            var win = new Window
            {
                Title = "Trajectory of L0 = " + seed,
                Width = 1000,
                Height = 760,
                MinWidth = 620,
                MinHeight = 460
            };

            var steps = Num(40, 1, 1_000_000, 1);
            var cores = Num(CoreCount, 1, CoreCount, 1, 130);
            var engine = Combo(220, EngineList4, 0);
            var spill = new CheckBox { Content = "auto-spill", IsChecked = true };
            var metric = Combo(220, new[] { "s_n (counter)", "nu(L_n)", "|L_n| length", "log10 V(L_n)", "omega(L_n)" }, 0);
            var logY = new CheckBox { Content = "log Y", IsChecked = false };
            var replot = new Button { Content = "Recompute / plot" };
            var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.WarningBrush };
            var bar = Bar();
            var chart = new ChartControl { MinHeight = 280 };
            var table = Mono();
            var sline = new SelectableTextBlock { FontFamily = MonoFont, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            CancellationTokenSource cts = null;
            List<StepInfo> lastRows = null;

            async Task Compute()
            {
                cts?.Cancel(); cts = new CancellationTokenSource();
                var ct = cts.Token;
                int st = IntOf(steps);
                int cs2 = Math.Max(1, Math.Min(CoreCount, IntOf(cores)));
                int m = metric.SelectedIndex;
                var eng = EngineFrom4(engine.SelectedIndex);
                bool sp = spill.IsChecked == true;
                status.Text = "computing...";
                bar.Maximum = Math.Max(1, st); bar.Value = 0;
                var prog = new Progress<int>(n => bar.Value = n);
                try
                {
                    TrajectoryRunner runner = null;
                    var rows = await Task.Run(() =>
                    {
                        runner = new TrajectoryRunner
                        {
                            Seed = seed,
                            MaxSteps = st,
                            Engine = eng,
                            Cores = cs2,
                            AllowDiskFallback = sp,
                            ComputeOmega = true,
                            ComputeValue = true
                        };
                        return runner.Run(ct, prog);
                    }, ct);
                    lastRows = rows;

                    var cs = new ChartSeries { Name = "L0=" + seed, Color = Palette[0] };
                    foreach (var r in rows) cs.Values.Add(MetricValue(m, r));
                    chart.Series.Clear(); chart.Series.Add(cs);
                    chart.LogY = logY.IsChecked == true;
                    chart.Title = metric.SelectedItem?.ToString() ?? "";
                    chart.YLabel = chart.Title; chart.XOffset = 0; chart.XLabel = "step n";
                    chart.Refresh();

                    var tb = new StringBuilder();
                    var ss = new StringBuilder("s trajectory:  ");
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var r = rows[i];
                        ss.Append(r.Halted && r.S < 2 ? r.S + "(halt)" : r.S.ToString());
                        if (i < rows.Count - 1) ss.Append(", ");
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
                    string tail = "";
                    if (runner != null && runner.SpilledToDisk) tail = $"  [spilled to disk at n={runner.SpillStep}]";
                    status.Text = (last.Halted ? $"halts at step {last.N} (s={last.S})" : $"{rows.Count} steps, no halt yet") + tail;
                }
                catch (OperationCanceledException) { status.Text = "cancelled"; }
                catch (Exception ex) { status.Text = "error: " + ex.Message; }
            }

            string Export()
            {
                if (lastRows == null) return "(no trajectory yet)";
                var sb = new StringBuilder();
                sb.AppendLine($"# seed L0 = {seed}");
                sb.AppendLine("n\ts_n\tparity\tnu\tomega\tlength\tV");
                foreach (var r in lastRows)
                {
                    string v = r.HasValue ? r.Value.ToString(CultureInfo.InvariantCulture)
                                          : "~" + Value.DecimalDigits(r.Length) + "d";
                    sb.Append(r.N).Append('\t').Append(r.S).Append('\t')
                      .Append(r.ParityChar).Append('\t').Append(r.Nu).Append('\t')
                      .Append(r.Omega < 0 ? "-" : r.Omega.ToString()).Append('\t')
                      .Append(r.Length).Append('\t').Append(v).Append('\n');
                }
                return sb.ToString();
            }

            var expFile = new Button { Content = "Save to file…" };
            expFile.Click += async (_, __) => { try { await SaveTextAsync(win, Export(), $"trajectory_L{seed}.tsv"); } catch { } };
            var expClip = new Button { Content = "Copy" };
            expClip.Click += async (_, __) => { try { await ClipboardWriteAsync(win, Export()); } catch { } };
            var saveImg = new Button { Content = "Save graph (PNG)" };
            saveImg.Click += async (_, __) => { try { await SaveChartAsync(win, chart, $"trajectory_L{seed}.png"); } catch { } };

            replot.Click += async (_, __) => await Compute();
            metric.SelectionChanged += async (_, __) => await Compute();
            engine.SelectionChanged += async (_, __) => await Compute();
            logY.IsCheckedChanged += (_, __) => { chart.LogY = logY.IsChecked == true; chart.Refresh(); };

            var topPanel = new StackPanel { Margin = new Thickness(12, 12, 12, 4), Spacing = 4 };
            topPanel.Children.Add(Row(Lbl("steps"), steps, Lbl($"cores (of {CoreCount})"), cores, Lbl("engine"), engine, spill));
            topPanel.Children.Add(Row(Lbl("metric"), metric, logY, replot, expFile, expClip, saveImg));
            topPanel.Children.Add(Row(bar, status));
            topPanel.Children.Add(new ScrollViewer { Height = 56, Content = sline });

            var chartBorder = ChartBox(chart);
            chartBorder.Margin = new Thickness(12, 0, 12, 6);
            var tableScroll = new ScrollViewer { Content = table, Margin = new Thickness(12, 0, 12, 12), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };

            var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,2*,1.2*") };
            Grid.SetRow(topPanel, 0); Grid.SetRow(chartBorder, 1); Grid.SetRow(tableScroll, 2);
            grid.Children.Add(topPanel); grid.Children.Add(chartBorder); grid.Children.Add(tableScroll);
            win.Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(AppTheme.Bg0, 0), new GradientStop(AppTheme.Bg1, 1) }
            };
            win.Content = grid;
            win.Show();             // independent window (no owner)
            _ = Compute();
        }

        // ================================================================ CONJUGATE T
        private TextBox _cjSeed;
        private NumericUpDown _cjN, _cjCores;
        private ComboBox _cjEngine;
        private CheckBox _cjSpill;
        private SelectableTextBlock _cjOut;
        private TextBlock _cjStatus;
        private ProgressBar _cjBar;
        private CancellationTokenSource _cjCts;
        private string _cjLastText;

        private Control BuildConjugateTab()
        {
            _cjSeed = Tb("10", 260, "seed L0 (binary)");
            _cjN = Num(12, 1, 2000, 1);
            _cjCores = Num(CoreCount, 1, CoreCount, 1, 130);
            _cjEngine = Combo(220, EngineList4, 0);
            _cjSpill = new CheckBox { Content = "auto-spill", IsChecked = true };
            var run = new Button { Content = "Iterate T" };
            run.Click += async (_, __) => await RunConjugate();
            var cancel = new Button { Content = "Cancel" };
            cancel.Click += (_, __) => _cjCts?.Cancel();
            var expFile = ExportFileButton(() => _cjLastText ?? "(no run yet)", () => $"conjugate_L{_cjSeed.Text?.Trim()}.txt");
            var expClip = ClipboardButton(() => _cjLastText ?? "(no run yet)");
            _cjStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.WarningBrush };
            _cjBar = Bar();
            _cjOut = Mono();

            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(Row(Lbl("Seed L0"), _cjSeed, Lbl("iterations"), _cjN, Lbl($"cores (of {CoreCount})"), _cjCores));
            panel.Children.Add(Row(Lbl("cross-check engine"), _cjEngine, _cjSpill, run, cancel, expFile, expClip));
            panel.Children.Add(Row(_cjBar, _cjStatus));
            panel.Children.Add(new TextBlock
            {
                Foreground = AppTheme.TextMutedBrush,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 6),
                Text = "Integer conjugate of Collatz type: N0 = V(L0), N_{n+1} = T(N_n). Each N_n is cross-checked against " +
                       "the bit-string value V(L_n) where the chosen engine can still materialise it. T iteration itself is " +
                       "sequential; cores speed up the cross-check trajectory."
            });
            panel.Children.Add(new TextBlock { Text = "n   s_n   digits(N_n)   verified   N_n", FontFamily = MonoFont, Foreground = AppTheme.TextMutedBrush });
            panel.Children.Add(new ScrollViewer { Height = 440, Content = _cjOut, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
            return new ScrollViewer { Content = Card(panel, new Thickness(12)) };
        }

        private async Task RunConjugate()
        {
            _cjCts?.Cancel(); _cjCts = new CancellationTokenSource();
            var ct = _cjCts.Token;
            string seed = (_cjSeed.Text ?? "").Trim();
            int maxN = IntOf(_cjN);
            int cores = Math.Max(1, Math.Min(CoreCount, IntOf(_cjCores)));
            var eng = EngineFrom4(_cjEngine.SelectedIndex);
            bool spill = _cjSpill.IsChecked == true;
            _cjStatus.Text = "iterating...";
            _cjBar.Maximum = Math.Max(1, maxN); _cjBar.Value = 0;
            var prog = new Progress<int>(n => _cjBar.Value = n);
            try
            {
                string text = await Task.Run(() =>
                {
                    var truth = new Dictionary<int, BigInteger>();
                    var runner = new TrajectoryRunner
                    {
                        Seed = seed,
                        MaxSteps = maxN,
                        Engine = eng,
                        Cores = cores,
                        AllowDiskFallback = spill,
                        ComputeOmega = false,
                        ComputeValue = true
                    };
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
                _cjLastText = text;
                _cjBar.Value = _cjBar.Maximum;
                _cjStatus.Text = "done";
            }
            catch (OperationCanceledException) { _cjStatus.Text = "cancelled"; }
            catch (Exception ex) { _cjStatus.Text = "error: " + ex.Message; }
        }

        // ================================================================ closing handler
        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (!_cleanupDone)
            {
                e.Cancel = true;
                if (!_cleanupInProgress)
                {
                    _cleanupInProgress = true;
                    _ = CloseSequence();
                }
            }
            base.OnClosing(e);
        }

        private async Task CloseSequence()
        {
            try { _orbCts?.Cancel(); } catch { }
            try { _grCts?.Cancel(); } catch { }
            try { _svCts?.Cancel(); } catch { }
            try { _svMgCts?.Cancel(); } catch { }
            try { _cjCts?.Cancel(); } catch { }

            var msg = new TextBlock
            {
                Text = "Cleaning up temporary disk data — please wait...",
                FontSize = 16,
                Foreground = AppTheme.TextBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var detail = new TextBlock
            {
                Text = $"removing  {DiskWorkspace.Root}",
                FontSize = 11,
                Foreground = AppTheme.TextMutedBrush,
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var prog = new ProgressBar { IsIndeterminate = true, Width = 360, Height = 16, Margin = new Thickness(0, 14, 0, 0) };
            var panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { msg, detail, prog }
            };
            Content = panel;

            await Task.Run(() => DiskWorkspace.Cleanup());

            _cleanupDone = true;
            Close();
        }

        // ================================================================ formatting helpers
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