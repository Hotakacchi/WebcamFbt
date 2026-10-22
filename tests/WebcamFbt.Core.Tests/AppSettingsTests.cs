using System.Numerics;
using WebcamFbt.Core.Geometry;
using WebcamFbt.Core.Persistence;
using WebcamFbt.Core.Tracking;

namespace WebcamFbt.Core.Tests;

public class AppSettingsStoreTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"webcamfbt-settings-{Guid.NewGuid():N}.json");

    [Fact]
    public void 保存した設定を読み戻せる()
    {
        var path = TempFile();
        try
        {
            var s = new AppSettings
            {
                CameraDevice = 2,
                Width = 1920,
                Height = 1080,
                Backend = "DirectShow",
                TwoCameraMode = true,
                HeightMeters = 1.78,
                OscHost = "192.168.1.20",
                OscPort = 9001,
                BoardSquareMm = 23.5,
            };
            new AppSettingsStore(path).Save(s);
            var loaded = new AppSettingsStore(path).Load();

            Assert.Equal(2, loaded.CameraDevice);
            Assert.Equal(1920, loaded.Width);
            Assert.Equal("DirectShow", loaded.Backend);
            Assert.True(loaded.TwoCameraMode);
            Assert.Equal(1.78, loaded.HeightMeters);
            Assert.Equal("192.168.1.20", loaded.OscHost);
            Assert.Equal(9001, loaded.OscPort);
            Assert.Equal(23.5, loaded.BoardSquareMm);
            Assert.Null(loaded.BodyCalibration);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ファイルが無ければ既定値()
    {
        var loaded = new AppSettingsStore(TempFile()).Load();
        Assert.Equal(1.65, loaded.HeightMeters);
        Assert.Equal(9000, loaded.OscPort);
    }

    [Fact]
    public void 壊れたファイルは既定値で読む()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "{ \"HeightMeters\": ");
            Assert.Equal(1.65, new AppSettingsStore(path).Load().HeightMeters);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ファイルに無い項目は既定値になる()
    {
        var path = TempFile();
        try
        {
            // 古い版のファイル（一部の項目しかない）
            File.WriteAllText(path, "{ \"HeightMeters\": 1.80 }");
            var loaded = new AppSettingsStore(path).Load();
            Assert.Equal(1.80, loaded.HeightMeters);
            Assert.Equal(70, loaded.HorizontalFov);
            Assert.Equal("127.0.0.1", loaded.OscHost);
            Assert.True(loaded.UseStereoTracking);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class BodyCalibrationSettingsTests
{
    [Fact]
    public void 体のキャリブレーション結果を保存して戻せる()
    {
        var tilt = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.2f));
        var original = new CalibrationData(new BodyProportions(0.23, 0.12, 0.29, 0.26, 0.24, 0.14, 0.1), -1.25f, 7.5, tilt);

        var path = Path.Combine(Path.GetTempPath(), $"webcamfbt-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new AppSettingsStore(path);
            store.Save(new AppSettings { BodyCalibration = BodyCalibrationSettings.From(original) });
            var restored = store.Load().BodyCalibration!.ToCalibrationData();

            Assert.NotNull(restored);
            Assert.Equal(original.Body, restored!.Body);
            Assert.Equal(-1.25f, restored.FloorY);
            Assert.Equal(7.5, restored.YawOffsetDeg);
            Assert.True(OneEuroFilterRotation.AngleBetween(tilt, restored.Tilt!.Value) < 1e-6);
            Assert.True(restored.IsCalibrated);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 床も傾きも無い結果を戻せる()
    {
        var original = new CalibrationData(new BodyProportions(0.23, 0.12, 0.29, 0.26, 0.24, 0.14, 0.1), float.NaN, 0);
        var restored = BodyCalibrationSettings.From(original).ToCalibrationData();

        Assert.NotNull(restored);
        Assert.False(restored!.HasFloor);
        Assert.Null(restored.Tilt);
    }

    [Fact]
    public void おかしな値なら復元しない()
    {
        var s = BodyCalibrationSettings.From(new CalibrationData(BodyProportions.Default, 0, 0));
        s.Thigh = 0;
        Assert.Null(s.ToCalibrationData());

        var s2 = BodyCalibrationSettings.From(new CalibrationData(BodyProportions.Default, 0, 0));
        s2.Tilt = [1, 2];
        Assert.Null(s2.ToCalibrationData());
    }
}
