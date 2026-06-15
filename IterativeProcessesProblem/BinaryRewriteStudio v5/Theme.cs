using Avalonia.Media;

namespace BinaryRewrite
{
    /// <summary>One place for the whole app's colours, so the chart and the UI agree.</summary>
    internal static class AppTheme
    {
        // backdrop / surfaces
        public static readonly Color Bg0 = Color.Parse("#0e1220");
        public static readonly Color Bg1 = Color.Parse("#141a2c");
        public static readonly Color Surface = Color.Parse("#1a2236");
        public static readonly Color SurfaceHi = Color.Parse("#232d46");
        public static readonly Color Border = Color.Parse("#2c3651");
        public static readonly Color BorderHi = Color.Parse("#3b4660");
        public static readonly Color ChartBg = Color.Parse("#101729");

        // accent + status
        public static readonly Color Accent = Color.Parse("#5b8cff");
        public static readonly Color AccentHi = Color.Parse("#7aa2ff");
        public static readonly Color AccentLo = Color.Parse("#3f6fe0");
        public static readonly Color Text = Color.Parse("#e7eaf3");
        public static readonly Color TextMuted = Color.Parse("#98a2bb");
        public static readonly Color Grid = Color.Parse("#26304a");
        public static readonly Color Success = Color.Parse("#4ade80");
        public static readonly Color Danger = Color.Parse("#f87171");
        public static readonly Color Warning = Color.Parse("#fbbf24");

        // vibrant series palette
        public static readonly Color[] Series =
        {
            Color.Parse("#60a5fa"), Color.Parse("#f59e0b"), Color.Parse("#34d399"),
            Color.Parse("#f472b6"), Color.Parse("#a78bfa"), Color.Parse("#22d3ee"),
            Color.Parse("#fb7185"), Color.Parse("#facc15"),
        };

        public static readonly IBrush AccentBrush = new SolidColorBrush(Accent);
        public static readonly IBrush AccentHiBrush = new SolidColorBrush(AccentHi);
        public static readonly IBrush AccentLoBrush = new SolidColorBrush(AccentLo);
        public static readonly IBrush SurfaceBrush = new SolidColorBrush(Surface);
        public static readonly IBrush SurfaceHiBrush = new SolidColorBrush(SurfaceHi);
        public static readonly IBrush BorderBrush = new SolidColorBrush(Border);
        public static readonly IBrush BorderHiBrush = new SolidColorBrush(BorderHi);
        public static readonly IBrush TextBrush = new SolidColorBrush(Text);
        public static readonly IBrush TextMutedBrush = new SolidColorBrush(TextMuted);
        public static readonly IBrush SuccessBrush = new SolidColorBrush(Success);
        public static readonly IBrush DangerBrush = new SolidColorBrush(Danger);
        public static readonly IBrush WarningBrush = new SolidColorBrush(Warning);
        public static readonly IBrush ChartBgBrush = new SolidColorBrush(ChartBg);
    }
}