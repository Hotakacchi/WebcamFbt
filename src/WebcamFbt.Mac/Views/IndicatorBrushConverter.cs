using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using WebcamFbt.App.ViewModels;

namespace WebcamFbt.Mac.Views;

/// <summary>状態表示の色（緑=正常、黄=注意、赤=見えていない・エラー、灰=オフ）。色は Windows 版の App.xaml と同じ。</summary>
public sealed class IndicatorBrushConverter : IValueConverter
{
    public static IndicatorBrushConverter Instance { get; } = new();

    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#FF4CC46A"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#FFE8B33C"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#FFE5534B"));
    private static readonly IBrush Off = new SolidColorBrush(Color.Parse("#FF5A5F68"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        IndicatorState.Good => Good,
        IndicatorState.Warn => Warn,
        IndicatorState.Bad => Bad,
        _ => Off,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
