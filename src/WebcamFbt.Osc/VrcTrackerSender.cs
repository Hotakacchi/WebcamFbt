using System.Net;
using System.Net.Sockets;
using WebcamFbt.Core.Diagnostics;
using WebcamFbt.Core.Threading;
using WebcamFbt.Core.Tracking;

namespace WebcamFbt.Osc;

/// <summary>
/// VRChat の OSC トラッカーへ送信する。専用スレッドで、最新のトラッキング結果だけを送る。
///
/// 送るアドレス（VRChat 公式ドキュメント「OSC Trackers」より）:
///  - /tracking/trackers/{1〜8}/position, /rotation   … float×3（m、オイラー角[度]）
///  - /tracking/trackers/head/position                 … 毎フレーム。OSC 空間全体を頭に合わせる（補間なし）
///  - /tracking/trackers/head/rotation                 … 「向きを合わせる」を押したときだけ 1 回送る
///    （1 回だけ送ると、ヨーがその場で一度だけ合わせられる。300ms 以内に続けて送るとストリーミング扱いになる）
/// </summary>
public sealed class VrcTrackerSender : IDisposable
{
    private readonly LatestValue<TrackerFrame> _pending = new();
    private readonly UdpClient _udp = new();
    private Thread? _thread;
    private volatile bool _running;
    private volatile IPEndPoint _endpoint = new(IPAddress.Loopback, 9000);
    private int _alignRequested; // 1 なら次の送信で head の rotation を送る

    /// <summary>頭の位置を送るか（VRChat 側の位置合わせに使う）。</summary>
    public bool SendHeadPosition { get; set; } = true;

    /// <summary>1 秒あたりに送ったフレーム数。</summary>
    public RateCounter SendRate { get; } = new();

    /// <summary>最後に起きた送信エラー（なければ null）。</summary>
    public string? LastError { get; private set; }

    /// <summary>送信先を変える。</summary>
    public void SetEndpoint(string host, int port)
    {
        var address = IPAddress.TryParse(host, out var ip)
            ? ip
            : Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        _endpoint = new IPEndPoint(address, port);
    }

    public string Endpoint => _endpoint.ToString();

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(SendLoop)
        {
            IsBackground = true,
            Name = "OscSender",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>送るトラッキング結果を渡す（推論スレッドから呼ぶ）。</summary>
    public void Submit(TrackerFrame frame) => _pending.Put(frame);

    /// <summary>
    /// 向きの合わせ込みを要求する。利用者がカメラの方を向いた状態で押すと、
    /// 「今の HMD の向き ＝ カメラの方を向いた向き」として VRChat が OSC 空間を回転させる。
    /// </summary>
    public void RequestYawAlign() => Interlocked.Exchange(ref _alignRequested, 1);

    private void SendLoop()
    {
        while (_running)
        {
            var frame = _pending.Take(TimeSpan.FromMilliseconds(100));
            if (frame is null) continue;
            try
            {
                Send(frame);
                SendRate.Tick();
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Thread.Sleep(100);
            }
        }
    }

    private void Send(TrackerFrame frame)
    {
        var ep = _endpoint;
        foreach (var t in frame.Trackers)
        {
            if (!t.Valid) continue;
            // VRChat の座標系は Unity と同じ（+Y 上、左手系、m）なので、トラッキング座標をそのまま送る
            SendMessage(ep, $"/tracking/trackers/{t.OscIndex}/position", t.Position.X, t.Position.Y, t.Position.Z);
            SendMessage(ep, $"/tracking/trackers/{t.OscIndex}/rotation", t.EulerDeg.X, t.EulerDeg.Y, t.EulerDeg.Z);
        }

        if (SendHeadPosition && frame.HeadValid)
        {
            var h = frame.HeadPosition;
            SendMessage(ep, "/tracking/trackers/head/position", h.X, h.Y, h.Z);
        }

        if (Interlocked.Exchange(ref _alignRequested, 0) == 1)
        {
            // オイラー角 (0,0,0) が前向き。トラッキング座標の +Z は「カメラの方を向いた向き」
            SendMessage(ep, "/tracking/trackers/head/rotation", 0f, 0f, 0f);
        }
    }

    private void SendMessage(IPEndPoint ep, string address, float x, float y, float z)
    {
        var bytes = OscMessage.Encode(address, x, y, z);
        _udp.Send(bytes, bytes.Length, ep);
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(1));
        _thread = null;
        SendRate.Reset();
    }

    public void Dispose()
    {
        Stop();
        _pending.Dispose();
        _udp.Dispose();
    }
}
