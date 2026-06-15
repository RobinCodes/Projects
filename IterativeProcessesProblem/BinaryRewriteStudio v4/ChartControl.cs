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
        public double Thickness = 1.8;
        public double MarkerRadius = 2.6;
    }

    /// <summary>
    /// Lightweight dependency-free line chart. X axis is the step index n; one or more
    /// Y series are overlaid. Supports linear and log10 Y scaling. Redraw via Refresh().
    /// </summary>
    public sealed class ChartControl : Control
    {
        public List<ChartSeries> Series { get; } = new List<ChartSeries>();
        public bool LogY { get; set; }
        public bool ShowLegend { get; set; } = true;
        public int XOffset { get; set; } = 0;        // added to X tick labels (e.g., to show 'length' instead of 'index')
        public string Title { get; set; } = "";
        public string XLabel { get; set; } = "step n";
        public string YLabel { get; set; } = "";

        private static readonly Typeface Mono = new Typeface(FontFamily.Default);

        public void Refresh() => InvalidateVisual();

        public override void Render(DrawingContext g)
        {
            var bg = new SolidColorBrush(Color.FromRgb(0x1b, 0x1f, 0x27));
            var rect = new Rect(Bounds.Size);
            g.FillRectangle(bg, rect);

            double W = Bounds.Width, H = Bounds.Height;
            if (W < 60 || H < 60) return;

            double mL = 70, mR = 16, mT = Title.Length > 0 ? 34 : 14, mB = 46;
            double plotW = W - mL - mR, plotH = H - mT - mB;
            if (plotW <= 10 || plotH <= 10) return;

            var axisBrush = new SolidColorBrush(Color.FromRgb(0xc8, 0xcd, 0xd6));
            var gridBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x3a, 0x46));
            var axisPen = new Pen(axisBrush, 1.2);
            var gridPen = new Pen(gridBrush, 1);

            // ---- data bounds ----
            int maxLen = 0;
            double yMin = double.PositiveInfinity, yMax = double.NegativeInfinity;
            foreach (var s in Series)
            {
                if (s.Values.Count > maxLen) maxLen = s.Values.Count;
                foreach (var v in s.Values)
                {
                    double y = Transform(v);
                    if (double.IsNaN(y) || double.IsInfinity(y)) continue;
                    if (y < yMin) yMin = y;
                    if (y > yMax) yMax = y;
                }
            }
            if (maxLen == 0 || double.IsInfinity(yMin))
            {
                DrawText(g, "no data", new Point(mL + 8, mT + 8), axisBrush, 13);
                return;
            }
            if (yMax - yMin < 1e-9) { yMax += 1; yMin -= 1; }
            double pad = (yMax - yMin) * 0.06; yMin -= pad; yMax += pad;
            int xMax = Math.Max(1, maxLen - 1);

            Func<double, double> sx = i => mL + plotW * (i / xMax);
            Func<double, double> sy = y => mT + plotH * (1.0 - (y - yMin) / (yMax - yMin));

            // ---- gridlines + Y labels ----
            int yticks = 5;
            for (int k = 0; k <= yticks; k++)
            {
                double yv = yMin + (yMax - yMin) * k / yticks;
                double py = sy(yv);
                g.DrawLine(gridPen, new Point(mL, py), new Point(mL + plotW, py));
                string lab = LogY ? FmtPow(yv) : Fmt(yv);
                var ft = MakeText(lab, axisBrush, 11);
                g.DrawText(ft, new Point(mL - ft.Width - 6, py - ft.Height / 2));
            }
            // ---- X labels ----
            int xticks = Math.Min(xMax, 10);
            for (int k = 0; k <= xticks; k++)
            {
                int xv = (int)Math.Round((double)xMax * k / xticks);
                double px = sx(xv);
                g.DrawLine(gridPen, new Point(px, mT), new Point(px, mT + plotH));
                var ft = MakeText((xv + XOffset).ToString(), axisBrush, 11);
                g.DrawText(ft, new Point(px - ft.Width / 2, mT + plotH + 6));
            }

            // ---- axes ----
            g.DrawLine(axisPen, new Point(mL, mT), new Point(mL, mT + plotH));
            g.DrawLine(axisPen, new Point(mL, mT + plotH), new Point(mL + plotW, mT + plotH));

            // ---- title / axis captions ----
            if (Title.Length > 0)
                g.DrawText(MakeText(Title, axisBrush, 15), new Point(mL, 8));
            g.DrawText(MakeText(XLabel, axisBrush, 12),
                new Point(mL + plotW / 2 - 24, mT + plotH + 24));
            if (YLabel.Length > 0)
                g.DrawText(MakeText(YLabel + (LogY ? " (log10)" : ""), axisBrush, 12), new Point(6, mT - 2));

            // ---- series ----
            foreach (var s in Series)
            {
                var brush = new SolidColorBrush(s.Color);
                var pen = new Pen(brush, s.Thickness);
                Point? prev = null;
                bool drawMarkersForThis = !s.DrawLines || (s.ShowMarkers && maxLen <= 60);
                for (int i = 0; i < s.Values.Count; i++)
                {
                    double y = Transform(s.Values[i]);
                    if (double.IsNaN(y) || double.IsInfinity(y)) { prev = null; continue; }
                    var p = new Point(sx(i), sy(y));
                    if (s.DrawLines && prev.HasValue) g.DrawLine(pen, prev.Value, p);
                    if (drawMarkersForThis) g.DrawEllipse(brush, null, p, s.MarkerRadius, s.MarkerRadius);
                    prev = p;
                }
            }

            // ---- legend ----
            if (ShowLegend)
            {
                double ly = mT + 4;
                foreach (var s in Series)
                {
                    var brush = new SolidColorBrush(s.Color);
                    g.FillRectangle(brush, new Rect(mL + plotW - 150, ly + 3, 14, 4));
                    g.DrawText(MakeText(s.Name, axisBrush, 11), new Point(mL + plotW - 132, ly - 3));
                    ly += 16;
                }
            }
        }

        private double Transform(double v)
        {
            if (!LogY) return v;
            return v > 0 ? Math.Log10(v) : double.NaN;
        }

        private static string Fmt(double v)
        {
            double a = Math.Abs(v);
            if (a >= 1e6 || (a > 0 && a < 1e-3)) return v.ToString("0.##e+0", CultureInfo.InvariantCulture);
            if (a >= 1000) return v.ToString("#,0", CultureInfo.InvariantCulture);
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string FmtPow(double log10) =>
            "1e" + Math.Round(log10).ToString(CultureInfo.InvariantCulture);

        private FormattedText MakeText(string t, IBrush b, double size) =>
            new FormattedText(t, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Mono, size, b);

        private void DrawText(DrawingContext g, string t, Point p, IBrush b, double size) =>
            g.DrawText(MakeText(t, b, size), p);
    }
}
