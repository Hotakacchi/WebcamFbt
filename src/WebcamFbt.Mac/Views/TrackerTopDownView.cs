using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using WebcamFbt.Core.Tracking;
using Point = Avalonia.Point;

namespace WebcamFbt.Mac.Views;

/// <summary>
/// トラッカーを上から見た図。Windows 版（WebcamFbt.App/Views/TrackerTopDownView.cs）を Avalonia で描き直したもの。
/// 腰を中心に、両足・頭の位置と、腰・足の向きを描く。画面の上がカメラ側（トラッキング座標の +Z）、右が体の右（+X）。
/// 色はプレビューの骨格と同じ（左 = 水色、右 = オレンジ、中央 = 黄緑）。
/// </summary>
public sealed class TrackerTopDownView : Control
{
    public static readonly StyledProperty<TrackerFrame?> FrameProperty =
        AvaloniaProperty.Register<TrackerTopDownView, TrackerFrame?>(nameof(Frame));

    static TrackerTopDownView()
    {
        AffectsRender<TrackerTopDownView>(FrameProperty);
    }

    public TrackerFrame? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    /// <summary>中心から端までに映す距離 [m]。</summary>
    public double RangeMeters { get; set; } = 0.8;

    private static readonly IBrush Background = new SolidColorBrush(Color.FromArgb(0xAA, 0, 0, 0));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAA));
    private static readonly IBrush CenterBrush = new SolidColorBrush(Color.FromRgb(120, 230, 80));
    private static readonly IBrush LeftBrush = new SolidColorBrush(Color.FromRgb(0, 200, 255));
    private static readonly IBrush RightBrush = new SolidColorBrush(Color.FromRgb(255, 140, 0));
    private static readonly IBrush HeadBrush = Brushes.White;
    private static readonly Typeface LabelFont = new("Hiragino Sans, $Default");

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(Background, null, new Rect(0, 0, w, h), 6, 6);

        var center = new Point(w / 2, h / 2 + 6);
        double scale = (Math.Min(w, h) / 2 - 14) / RangeMeters;

        // 目盛り: 25cm ごとの円と十字
        for (double r = 0.25; r <= RangeMeters + 1e-6; r += 0.25) dc.DrawEllipse(null, GridPen, center, r * scale, r * scale);
        dc.DrawLine(GridPen, new Point(center.X, center.Y - RangeMeters * scale), new Point(center.X, center.Y + RangeMeters * scale));
        dc.DrawLine(GridPen, new Point(center.X - RangeMeters * scale, center.Y), new Point(center.X + RangeMeters * scale, center.Y));
        DrawText(dc, "↑ カメラ側", new Point(w / 2, 4));

        var frame = Frame;
        TrackerPose? hip = frame?.Trackers.FirstOrDefault(t => t.Role == TrackerRole.Hip);
        if (frame == null || hip is not { Valid: true } hipPose)
        {
            DrawText(dc, "腰が見えていません", new Point(w / 2, h / 2 - 8));
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
    private static void DrawDirection(DrawingContext dc, Point at, Quaternion rotation, double length, IBrush brush)
    {
        var forward = Vector3.Transform(Vector3.UnitZ, rotation);
        var flat = new Vector2(forward.X, forward.Z);
        if (flat.LengthSquared() < 1e-6f) return; // 真上・真下を向いている
        flat = Vector2.Normalize(flat);
        var end = new Point(at.X + flat.X * length, at.Y - flat.Y * length);
        dc.DrawLine(new Pen(brush, 2.5), at, end);
    }

    /// <summary>文字を、指定した点を上辺の中央にして描く。</summary>
    private static void DrawText(DrawingContext dc, string text, Point topCenter)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFont, 11, TextBrush);
        dc.DrawText(formatted, new Point(topCenter.X - formatted.Width / 2, topCenter.Y));
    }
}
