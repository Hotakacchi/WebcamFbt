using WebcamFbt.Core.Geometry;
using WebcamFbt.Core.Persistence;
using WebcamFbt.Core.Updates;

namespace WebcamFbt.Core.Tests;

public class UpdateCheckerTests
{
    // GitHub API（/repos/{owner}/{repo}/releases）の応答の形（使う項目だけ）
    private const string Releases = """
        [
          { "tag_name": "v0.3.0", "name": "WebcamFbt v0.3.0（下書き）", "html_url": "https://example.com/v0.3.0", "draft": true,  "prerelease": false },
          { "tag_name": "v0.2.0", "name": "WebcamFbt v0.2.0",           "html_url": "https://example.com/v0.2.0", "draft": false, "prerelease": true },
          { "tag_name": "v0.1.2", "name": "",                           "html_url": "https://example.com/v0.1.2", "draft": false, "prerelease": false },
          { "tag_name": "v0.1.1", "name": "WebcamFbt v0.1.1",           "html_url": "https://example.com/v0.1.1", "draft": false, "prerelease": true },
          { "tag_name": "nightly", "name": "版ではないタグ",             "html_url": "https://example.com/nightly", "draft": false, "prerelease": true }
        ]
        """;

    [Fact]
    public void 下書きを除いた一番新しい版を選ぶ()
    {
        var update = UpdateChecker.FindNewer(Releases, "0.1.1");

        Assert.NotNull(update);
        Assert.Equal("0.2.0", update!.Version);   // 0.3.0 は下書きなので選ばない。プレリリースは含める
        Assert.Equal("WebcamFbt v0.2.0", update.Title);
        Assert.Equal("https://example.com/v0.2.0", update.PageUrl);
        Assert.True(update.IsPrerelease);
    }

    [Fact]
    public void 今が最新なら何も返さない()
    {
        Assert.Null(UpdateChecker.FindNewer(Releases, "0.2.0"));
        Assert.Null(UpdateChecker.FindNewer(Releases, "1.0.0"));
    }

    [Fact]
    public void 題名が空ならタグを題名にする()
    {
        const string json = """[ { "tag_name": "v0.1.2", "name": "", "html_url": "https://example.com/v0.1.2", "draft": false, "prerelease": false } ]""";
        Assert.Equal("v0.1.2", UpdateChecker.FindNewer(json, "0.1.1")!.Title);
    }

    [Theory]
    [InlineData("v0.1.2", "0.1.2")]
    [InlineData("0.1.2", "0.1.2")]
    [InlineData("0.1.1+f71030a4ea", "0.1.1")]   // ビルドに埋め込まれる版（コミット付き）
    [InlineData("0.2.0-beta", "0.2.0")]
    [InlineData("1.2", "1.2.0")]
    public void 版の書き方の違いを吸収する(string text, string expected)
    {
        Assert.True(UpdateChecker.TryParseVersion(text, out var v));
        Assert.Equal(expected, v.ToString(3));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nightly")]
    public void 版でない文字は読まない(string? text) => Assert.False(UpdateChecker.TryParseVersion(text, out _));

    [Fact]
    public void コミット付きの今の版とも比べられる()
    {
        Assert.Equal("0.2.0", UpdateChecker.FindNewer(Releases, "0.1.1+f71030a4ea")!.Version);
    }
}

public class SettingsGuardTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"webcamfbt-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void 読めない設定ファイルは別名で残し_保存しても消えない()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            // 新しい版で型が変わって読めなくなった、などを想定（HeightMeters が数値でない）
            const string original = "{ \"HeightMeters\": \"高い\", \"OscHost\": \"192.168.1.20\" }";
            File.WriteAllText(path, original);

            var store = new AppSettingsStore(path);
            var loaded = store.Load();

            Assert.Equal(1.65, loaded.HeightMeters);           // 既定値で起動する
            Assert.NotNull(store.LoadProblem);
            Assert.NotNull(store.BackupPath);
            Assert.Equal(original, File.ReadAllText(store.BackupPath!)); // 元のファイルは中身ごと残る

            store.Save(loaded);                                // 既定値で保存しても
            Assert.Equal(original, File.ReadAllText(store.BackupPath!)); // 残したものは消えない
            Assert.Equal(1.65, new AppSettingsStore(path).Load().HeightMeters);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 読めるファイルなら別名は作らない()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var store = new AppSettingsStore(path);
            store.Save(new AppSettings { HeightMeters = 1.8 });
            var again = new AppSettingsStore(path);
            Assert.Equal(1.8, again.Load().HeightMeters);
            Assert.Null(again.LoadProblem);
            Assert.Null(again.BackupPath);
            Assert.Single(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 読めないカメラの校正ファイルも別名で残す()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "calibration.json");
            const string original = "{ \"Cameras\": \"壊れている\" }";
            File.WriteAllText(path, original);

            var store = new CameraCalibrationStore(path);
            store.SaveCamera(0, new CameraCalibration(1280, 720, 900, 900, 640, 360, [0, 0, 0, 0, 0], 0.3, 15));

            Assert.NotNull(store.BackupPath);
            Assert.Equal(original, File.ReadAllText(store.BackupPath!));
            Assert.NotNull(new CameraCalibrationStore(path).GetCamera(0, 1280, 720));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 新しい版の確認の設定を保存して戻せる()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var when = new DateTime(2026, 10, 22, 6, 30, 0, DateTimeKind.Utc);
            new AppSettingsStore(path).Save(new AppSettings
            {
                CheckForUpdates = false,
                SkippedUpdateVersion = "0.1.2",
                LastUpdateCheckUtc = when,
            });
            var loaded = new AppSettingsStore(path).Load();
            Assert.False(loaded.CheckForUpdates);
            Assert.Equal("0.1.2", loaded.SkippedUpdateVersion);
            Assert.Equal(when, loaded.LastUpdateCheckUtc);

            // 古いファイル（項目が無い）なら確認はオン
            File.WriteAllText(path, "{}");
            Assert.True(new AppSettingsStore(path).Load().CheckForUpdates);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
