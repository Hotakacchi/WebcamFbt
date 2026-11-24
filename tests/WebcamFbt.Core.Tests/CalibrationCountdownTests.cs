using WebcamFbt.Core.Tracking;

namespace WebcamFbt.Core.Tests;

public class CalibrationCountdownTests
{
    [Fact]
    public void 五秒経過まで待ち開始は一度だけ通知する()
    {
        var timer = new CalibrationCountdown();
        timer.Start(10);
        Assert.Equal(5, timer.Poll(10));
        Assert.Equal(1, timer.Poll(14.999));
        Assert.Equal(0, timer.Poll(15));
        Assert.Null(timer.Poll(16));
    }

    [Fact]
    public void 中止した場合は計測を開始しない()
    {
        var timer = new CalibrationCountdown();
        timer.Start(10);
        timer.Cancel();
        Assert.Null(timer.Poll(20));
    }
}
