using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using WebcamFbt.Core.Tracking;

namespace WebcamFbt.App.Views;

/// <summary>
/// トラッカーを上から見た図（フェーズ 5b）。腰を中心に、両足・頭の位置と、腰・足の向きを描く。
/// 画面の上がカメラ側（トラッキング座標の +Z）、右が体の右（+X）。奥行きや向きのずれを目で確かめる用。
/// 色はプレビューの骨格と同じ（左 = 水色、右 = オレンジ、中央 = 黄緑）。
/// </summary>
public sealed class TrackerTopDownView : FrameworkElement
{
    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
        nameof(Frame), typeof(TrackerFrame), typeof(TrackerTopDownView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public TrackerFrame? Frame
    {
        get => (TrackerFrame?)GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    /// <summary>中心から端までに映す距離 [m]。</summary>
    public double RangeMeters { get; set; } = 0.8;

    private static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromArgb(0xAA, 0, 0, 0)));
    private static readonly Pen GridPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))), 1));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAA)));
    private static readonly Brush CenterBrush = Frozen(new SolidColorBrush(Color.FromRgb(120, 230, 80)));
    private static readonly Brush LeftBrush = Frozen(new SolidColorBrush(Color.FromRgb(0, 200, 255)));
    private static readonly Brush RightBrush = Frozen(new SolidColorBrush(Color.FromRgb(255, 140, 0)));
    private static readonly Brush HeadBrush = Frozen(new SolidColorBrush(Colors.White));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRoundedRectangle(Background, null, new Rect(0, 0, w, h), 6, 6);

        var center = new Point(w / 2, h / 2 + 6);
        double scale = (System.Math.Min(w, h) / 2 - 14) / RangeMeters;

        // 目盛り: 25cm ごとの円と十字
        for (double r = 0.25; r <= RangeMeters + 1e-6; r += 0.25) dc.DrawEllipse(null, GridPen, center, r * scale, r * scale);
        dc.DrawLine(GridPen, new Point(center.X, center.Y - RangeMeters * scale), new Point(center.X, center.Y + RangeMeters * scale));
        dc.DrawLine(GridPen, new Point(center.X - RangeMeters * scale, center.Y), new Point(center.X + RangeMeters * scale, center.Y));
        DrawText(dc, "↑ カメラ側", new Point(w / 2, 4), center: true);

        var frame = Frame;
        TrackerPose? hip = frame?.Trackers.FirstOrDefault(t => t.Role == TrackerRole.Hip);
        if (frame == null || hip is not { Valid: true } hipPose)
        {
            DrawText(dc, "腰が見えていません", new Point(w / 2, h / 2 - 8), center: true);
            return;
        }

        var origin = hipPose.Position;
        Point ToScreen(Vector3 p) => new(center.X + (p.X - origin.X) * scale, center.Y - (p.Z - origin.Z) * scale);

        foreach (var t in frame.Trackers)
        {
            if (!t.Valid || t.Role == TrackerRole.Hip) continue;
            var brush = t.Role == TrackerRole.LeftFoot ? LeftBrush : RightBrush;
            var p = ToScreen(t.Position);
            DrawDirection(dc, p, t.Rotation, 14, brush);
            dc.DrawEllipse(brush, null, p, 5, 5);
        }

        if (frame.HeadValid) dc.DrawEllipse(HeadBrush, null, ToScreen(frame.HeadPosition), 4, 4);

        DrawDirection(dc, center, hipPose.Rotation, 24, CenterBrush);
        dc.DrawEllipse(CenterBrush, null, center, 6, 6);
    }

    /// <summary>回転の前方向（+Z）を、上から見た向きの線として描く。</summary>
    private static void DrawDirection(DrawingContext dc, Point at, Quaternion rotation, double length, Brush brush)
    {
        var forward = Vector3.Transform(Vector3.UnitZ, rotation);
        var flat = new Vector2(forward.X, forward.Z);
        if (flat.LengthSquared() < 1e-6f) return; // 真上・真下を向いている
        flat = Vector2.Normalize(flat);
        var end = new Point(at.X + flat.X * length, at.Y - flat.Y * length);
        dc.DrawLine(new Pen(brush, 2.5), at, end);
    }

    private void DrawText(DrawingContext dc, string text, Point at, bool center)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Meiryo"), 11, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, center ? new Point(at.X - formatted.Width / 2, at.Y) : at);
    }
}
