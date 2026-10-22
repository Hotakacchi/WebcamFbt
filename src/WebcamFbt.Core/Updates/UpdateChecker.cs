using System.Net.Http.Headers;
using System.Text.Json;

namespace WebcamFbt.Core.Updates;

/// <summary>新しい版の情報。</summary>
/// <param name="Version">版（例: 0.1.2）。</param>
/// <param name="Title">リリースの題名。</param>
/// <param name="PageUrl">リリースのページ（ダウンロードはここから）。</param>
public sealed record UpdateInfo(string Version, string Title, string PageUrl, bool IsPrerelease);

/// <summary>確認の結果。</summary>
public enum UpdateCheckStatus
{
    /// <summary>今の版が最新。</summary>
    UpToDate,

    /// <summary>新しい版がある。</summary>
    Available,

    /// <summary>確認できなかった（通信できない、リポジトリが非公開など）。</summary>
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update = null, string? Problem = null);

/// <summary>
/// GitHub Releases に、今より新しい版が出ていないかを問い合わせる。
/// 下書きは無視し、プレリリースも含める（v1.0 までは全部プレリリースで出すため）。
/// 送るのはアプリ名と版（User-Agent。GitHub の API が必須にしている）だけで、利用者の情報は送らない。
/// </summary>
public static class UpdateChecker
{
    public const string Owner = "Hotakacchi";
    public const string Repository = "WebcamFbt";

    /// <summary>リリースの一覧のページ（利用者に開いてもらう用）。</summary>
    public static string ReleasesPage => $"https://github.com/{Owner}/{Repository}/releases";

    private static string ReleasesApi => $"https://api.github.com/repos/{Owner}/{Repository}/releases?per_page=20";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>問い合わせる。例外は投げず、確認できなければ <see cref="UpdateCheckStatus.Failed"/> を返す。</summary>
    public static async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("WebcamFbt", currentVersion));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 404 はリポジトリが非公開（または無い）とき
                return new UpdateCheckResult(UpdateCheckStatus.Failed,
                    Problem: response.StatusCode == System.Net.HttpStatusCode.NotFound
                        ? "リリースの情報が見つかりません"
                        : $"GitHub が応答しませんでした（{(int)response.StatusCode}）");
            }
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var newer = FindNewer(json, currentVersion);
            return newer is null
                ? new UpdateCheckResult(UpdateCheckStatus.UpToDate)
                : new UpdateCheckResult(UpdateCheckStatus.Available, newer);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Problem: "インターネットにつながっていないか、GitHub に届きませんでした");
        }
    }

    /// <summary>
    /// リリースの一覧（GitHub API の JSON）から、今より新しい版のうち一番新しいものを選ぶ。無ければ null。
    /// </summary>
    public static UpdateInfo? FindNewer(string releasesJson, string currentVersion)
    {
        if (!TryParseVersion(currentVersion, out var current)) return null;

        using var doc = JsonDocument.Parse(releasesJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        UpdateInfo? best = null;
        Version? bestVersion = null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (Bool(release, "draft")) continue;
            var tag = Str(release, "tag_name");
            if (!TryParseVersion(tag, out var version) || version <= current) continue;
            if (bestVersion is not null && version <= bestVersion) continue;

            bestVersion = version;
            var name = Str(release, "name");
            best = new UpdateInfo(
                version.ToString(3),
                string.IsNullOrWhiteSpace(name) ? tag! : name!,
                Str(release, "html_url") ?? ReleasesPage,
                Bool(release, "prerelease"));
        }
        return best;
    }

    /// <summary>
    /// 「v0.1.2」「0.1.2」「0.1.2+abc」「0.1.2-beta」などから版を取り出す（3 桁にそろえる）。
    /// </summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(['+', '-', ' ']);
        if (cut >= 0) s = s[..cut];
        if (!Version.TryParse(s, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
