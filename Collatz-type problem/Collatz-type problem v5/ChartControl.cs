using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace BinaryRewrite
{
    public sealed class ChartSeries
    {
        public string Name;
        public Color Color;
        public List<double> Values = new List<double>(); // indexed by step n (x = index)
        public bool ShowMarkers = true;
        public bool DrawLines = true;       // when false, render as point cloud only
        public double Thickness = 2.0;
        public double MarkerRadius = 2.8;
    }

    /// <summary>
    /// Lightweight, dependency-free line/scatter chart with "nice" integer-friendly ticks
    /// and a modern dark look. X is the step index n; one or more Y series overlay.
    /// </summary>
    public sealed class ChartControl : Control
    {
        public List<ChartSeries> Series { get; } = new List<ChartSeries>();
        public bool LogY { get; set; }
        public bool ShowLegend { get; set; } = true;
        public int XOffset { get; set; } = 0;     // added to X tick labels
        public string Title { get; set; } = "";
        public string XLabel { get; set; } = "step n";
        public string YLabel { get; set; } = "";

        private static readonly Typeface UiFace = new Typeface(FontFamily.Default);

        public void Refresh() => InvalidateVisual();

        public override void Render(DrawingContext g)
        {
            double W = Bounds.Width, H = Bounds.Height;
            var full = new Rect(0, 0, W, H);
            // panel background (rounded)
            g.DrawRectangle(new SolidColorBrush(AppTheme.ChartBg), null, full, 12, 12);
            if (W < 80 || H < 80) return;

            var axisBrush = new SolidColorBrush(AppTheme.TextMuted);
            var titleBrush = new SolidColorBrush(AppTheme.Text);
            var gridPen = new Pen(new SolidColorBrush(AppTheme.Grid), 1);
            var axisPen = new Pen(new SolidColorBrush(AppTheme.Border), 1.4);

            double mL = 78, mR = 18, mT = Title.Length > 0 ? 40 : 18, mB = 50;
            double plotW = W - mL - mR, plotH = H - mT - mB;
            if (plotW <= 20 || plotH <= 20) return;

            // ---- data bounds ----
            int maxLen = 0;
            double yMinD = double.PositiveInfinity, yMaxD = double.NegativeInfinity;
            foreach (var s in Series)
            {
                if (s.Values.Count > maxLen) maxLen = s.Values.Count;
                foreach (var v in s.Values)
                {
                    double y = Transform(v);
                    if (double.IsNaN(y) || double.IsInfinity(y)) continue;
                    if (y < yMinD) yMinD = y;
                    if (y > yMaxD) yMaxD = y;
                }
            }
            if (maxLen == 0 || double.IsInfinity(yMinD))
            {
                g.DrawText(Text("no data", axisBrush, 13), new Point(mL + 8, mT + 8));
                if (Title.Length > 0) g.DrawText(Text(Title, titleBrush, 15, FontWeight.SemiBold), new Point(mL, 12));
                return;
            }

            int xMax = Math.Max(1, maxLen - 1);
            var (yLo, yHi, yStep) = NiceAxis(yMinD, yMaxD, 6);
            int xStep = (int)Math.Max(1, NiceStep(xMax, 8));

            double sx(double i) => mL + plotW * (i / xMax);
            double sy(double y) => mT + plotH * (1.0 - (y - yLo) / (yHi - yLo));

            // ---- horizontal gridlines + Y labels (nice steps) ----
            for (double yv = yLo; yv <= yHi + yStep * 0.5; yv += yStep)
            {
                double py = sy(yv);
                g.DrawLine(gridPen, new Point(mL, py), new Point(mL + plotW, py));
                string lab = LogY ? "1e" + FmtTick(yv, yStep) : FmtTick(yv, yStep);
                var ft = Text(lab, axisBrush, 11);
                g.DrawText(ft, new Point(mL - ft.Width - 8, py - ft.Height / 2));
            }
            // ---- vertical gridlines + X labels (nice integer steps, tight to data) ----
            for (int xv = 0; xv <= xMax; xv += xStep)
            {
                double px = sx(xv);
                g.DrawLine(gridPen, new Point(px, mT), new Point(px, mT + plotH));
                var ft = Text((xv + XOffset).ToString(), axisBrush, 11);
                g.DrawText(ft, new Point(px - ft.Width / 2, mT + plotH + 7));
            }

            // ---- axes ----
            g.DrawLine(axisPen, new Point(mL, mT), new Point(mL, mT + plotH));
            g.DrawLine(axisPen, new Point(mL, mT + plotH), new Point(mL + plotW, mT + plotH));

            // ---- titles ----
            if (Title.Length > 0) g.DrawText(Text(Title, titleBrush, 15, FontWeight.SemiBold), new Point(mL, 12));
            g.DrawText(Text(XLabel, axisBrush, 12), new Point(mL + plotW / 2 - 26, mT + plotH + 26));
            if (YLabel.Length > 0)
                g.DrawText(Text(YLabel + (LogY ? " (log10)" : ""), axisBrush, 12), new Point(8, mT - 4));

            // ---- series ----
            foreach (var s in Series)
            {
                var brush = new SolidColorBrush(s.Color);
                var pen = new Pen(brush, s.Thickness) { LineJoin = PenLineJoin.Round, LineCap = PenLineCap.Round };
                bool markers = !s.DrawLines || (s.ShowMarkers && maxLen <= 60);
                Point? prev = null;
                for (int i = 0; i < s.Values.Count; i++)
                {
                    double y = Transform(s.Values[i]);
                    if (double.IsNaN(y) || double.IsInfinity(y)) { prev = null; continue; }
                    var p = new Point(sx(i), sy(y));
                    if (s.DrawLines && prev.HasValue) g.DrawLine(pen, prev.Value, p);
                    if (markers) g.DrawEllipse(brush, null, p, s.MarkerRadius, s.MarkerRadius);
                    prev = p;
                }
            }

            // ---- legend (rounded chips) ----
            if (ShowLegend && Series.Count > 0)
            {
                double ly = mT + 6;
                foreach (var s in Series)
                {
                    var brush = new SolidColorBrush(s.Color);
                    g.DrawRectangle(brush, null, new Rect(mL + plotW - 158, ly + 2, 16, 7), 3, 3);
                    g.DrawText(Text(s.Name, axisBrush, 11), new Point(mL + plotW - 136, ly - 3));
                    ly += 18;
                }
            }
        }

        private double Transform(double v) => !LogY ? v : (v > 0 ? Math.Log10(v) : double.NaN);

        // ---- "nice numbers" tick selection (Heckbert) ----
        private static (double lo, double hi, double step) NiceAxis(double lo, double hi, int targetTicks)
        {
            if (double.IsNaN(lo) || double.IsNaN(hi) || double.IsInfinity(lo) || double.IsInfinity(hi))
                return (0, 1, 1);
            if (Math.Abs(hi - lo) < 1e-12)
            {
                double c = lo;
                double s = NiceNum(Math.Max(1, Math.Abs(c)), true);
                return (c - s, c + s, s);
            }
            double range = NiceNum(hi - lo, false);
            double step = NiceNum(range / Math.Max(1, targetTicks - 1), true);
            double nlo = Math.Floor(lo / step) * step;
            double nhi = Math.Ceiling(hi / step) * step;
            return (nlo, nhi, step);
        }

        private static double NiceStep(double span, int targetTicks)
            => NiceNum(Math.Max(1, span) / Math.Max(1, targetTicks - 1), true);

        private static double NiceNum(double x, bool round)
        {
            if (x <= 0) return 1;
            double exp = Math.Floor(Math.Log10(x));
            double f = x / Math.Pow(10, exp);
            double nf = round
                ? (f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10)
                : (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10);
            return nf * Math.Pow(10, exp);
        }

        private static string FmtTick(double v, double step)
        {
            // integer step -> integer labels (fixes the "0.6, 1.2, 1.8" problem)
            if (Math.Abs(step - Math.Round(step)) < 1e-9 && Math.Abs(v - Math.Round(v)) < 1e-9)
            {
                long iv = (long)Math.Round(v);
                return Math.Abs(iv) >= 1_000_000 ? iv.ToString("0.##e+0", CultureInfo.InvariantCulture)
                                                 : iv.ToString("#,0", CultureInfo.InvariantCulture);
            }
            double a = Math.Abs(v);
            if (a >= 1e6) return v.ToString("0.##e+0", CultureInfo.InvariantCulture);
            int decimals = step >= 1 ? 0 : Math.Min(6, (int)Math.Ceiling(-Math.Log10(step)));
            return v.ToString("0." + new string('#', Math.Max(1, decimals)), CultureInfo.InvariantCulture);
        }

        private FormattedText Text(string t, IBrush b, double size, FontWeight weight = FontWeight.Normal)
        {
            var tf = weight == FontWeight.Normal ? UiFace : new Typeface(FontFamily.Default, FontStyle.Normal, weight);
            return new FormattedText(t, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, size, b);
        }
    }
}