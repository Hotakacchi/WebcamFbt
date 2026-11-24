using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using OpenCvSharp;
using WebcamFbt.Core.Diagnostics;
using WebcamFbt.Core.Threading;

namespace WebcamFbt.Vision.Camera;

/// <summary>同一LANのブラウザからHTTPSで送られたJPEGをカメラ2として受け取る。</summary>
public sealed class PhoneCameraSource : ICameraSource
{
    private const int MaxImageBytes = 1_500_000;
    private const int MaxHeaderBytes = 16_384;
    private readonly int _port;
    private readonly IPAddress? _bindAddress;
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private Task? _worker;
    private X509Certificate2? _root, _server;
    private long _frameId;
    private readonly object _clientsLock = new();
    private readonly HashSet<Task> _clients = [];
    private readonly SemaphoreSlim _clientSlots = new(8);

    public LatestSlot<Frame> Frames { get; } = new();
    public RateCounter CaptureRate { get; } = new();
    public string ConnectionUrl { get; private set; } = "";
    public string CertificatePath { get; private set; } = "";
    public string CertificateFingerprint { get; private set; } = "";
    public event Action<string>? Error;

    public PhoneCameraSource(int port = 8765, IPAddress? bindAddress = null) { _port = port; _bindAddress = bindAddress; }

    public void Start()
    {
        if (_listener != null) return;
        var address = _bindAddress ?? FindAddress() ?? throw new InvalidOperationException("利用できるLANのIPv4アドレスが見つかりません。");
        if (address.AddressFamily != AddressFamily.InterNetwork || !(IsPrivate(address) || IPAddress.IsLoopback(address)))
            throw new InvalidOperationException("受信先にはこのPCのプライベートIPv4アドレスを指定してください。");
        var listener = new TcpListener(address, _port);
        try
        {
            listener.Start();
            CreateCertificates(address);
            _stop = new CancellationTokenSource();
            _listener = listener;
            ConnectionUrl = $"https://{address}:{((IPEndPoint)listener.LocalEndpoint).Port}/#{_token}";
            var stopToken = _stop.Token;
            _worker = Task.Run(() => AcceptLoop(listener, stopToken));
        }
        catch
        {
            listener.Stop();
            _root?.Dispose(); _server?.Dispose();
            if (CertificatePath.Length > 0) { try { File.Delete(CertificatePath); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            throw;
        }
    }

    private static IPAddress? FindAddress() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a));

    private static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] >= 16 && b[1] <= 31;
    }

    private void CreateCertificates(IPAddress address)
    {
        using var rootKey = RSA.Create(2048);
        var ca = new CertificateRequest("CN=WebcamFbt Camera Session", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        ca.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        ca.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        ca.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(ca.PublicKey, false));
        _root = ca.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        using var serverKey = RSA.Create(2048);
        var req = new CertificateRequest("CN=WebcamFbt Camera", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(address);
        req.CertificateExtensions.Add(san.Build());
        using var publicServer = req.Create(_root, DateTimeOffset.UtcNow.AddMinutes(-4), DateTimeOffset.UtcNow.AddHours(23), RandomNumberGenerator.GetBytes(16));
        // Windows の Schannel は一時CNG鍵を直接使えないため、ユーザー鍵ストアに一時インポートする。
        using var withKey = publicServer.CopyWithPrivateKey(serverKey);
        _server = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebcamFbt");
        Directory.CreateDirectory(folder);
        CertificatePath = Path.Combine(folder, $"phone-camera-{Guid.NewGuid():N}.cer");
        File.WriteAllBytes(CertificatePath, _root.Export(X509ContentType.Cert));
        CertificateFingerprint = _root.GetCertHashString(HashAlgorithmName.SHA256);
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(stop);
                if (!_clientSlots.Wait(0)) { client.Dispose(); continue; }
                var task = HandleConnection(client, stop);
                lock (_clientsLock) _clients.Add(task);
                _ = task.ContinueWith(t => { lock (_clientsLock) _clients.Remove(t); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (SocketException) when (stop.IsCancellationRequested) { break; }
        }
        Task[] clients;
        lock (_clientsLock) clients = [.. _clients];
        await Task.WhenAll(clients);
    }

    private async Task HandleConnection(TcpClient client, CancellationToken stop)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try { await HandleClient(client, timeout.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException) { Error?.Invoke($"スマホとの接続エラー: {ex.Message}"); }
            catch (Exception ex) { Error?.Invoke($"スマホカメラの受信エラー: {ex.Message}"); }
            finally { _clientSlots.Release(); }
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        using var tls = new SslStream(client.GetStream(), false);
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = _server,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, ct);
        var headerBytes = new List<byte>();
        var one = new byte[1];
        while (headerBytes.Count < MaxHeaderBytes)
        {
            if (await tls.ReadAsync(one, ct) == 0) return;
            headerBytes.Add(one[0]);
            int n = headerBytes.Count;
            if (n >= 4 && headerBytes[n-4] == 13 && headerBytes[n-3] == 10 && headerBytes[n-2] == 13 && headerBytes[n-1] == 10) break;
        }
        if (headerBytes.Count >= MaxHeaderBytes) return;
        var lines = Encoding.ASCII.GetString(headerBytes.ToArray()).Split("\r\n");
        var request = lines[0].Split(' ');
        if (request.Length < 2) return;
        if (request[0] == "GET" && request[1] == "/") { await Respond(tls, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page), ct); return; }
        if (request[0] != "POST" || request[1] != "/frame") { await Respond(tls, 404, "text/plain", [], ct); return; }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && !headers.TryAdd(line[..colon].Trim(), line[(colon+1)..].Trim())) return;
        }
        if (!headers.TryGetValue("X-Camera-Token", out var token) || token != _token)
        { await Respond(tls, 403, "text/plain", [], ct); return; }
        byte[] data;
        bool hasLength = headers.TryGetValue("Content-Length", out var lengthText);
        bool hasEncoding = headers.TryGetValue("Transfer-Encoding", out var encoding);
        if (hasEncoding && !hasLength && string.Equals(encoding, "chunked", StringComparison.OrdinalIgnoreCase))
        {
            data = await ReadChunked(tls, ct) ?? [];
        }
        else if (hasLength && !hasEncoding && int.TryParse(lengthText, out int length) && length > 0 && length <= MaxImageBytes)
        {
            data = new byte[length];
            await tls.ReadExactlyAsync(data, ct);
        }
        else { await Respond(tls, 413, "text/plain", [], ct); return; }
        if (data.Length == 0) { await Respond(tls, 400, "text/plain", [], ct); return; }
        long received = Stopwatch.GetTimestamp();
        if (!ValidJpegSize(data)) { await Respond(tls, 400, "text/plain", [], ct); return; }
        using var decoded = Cv2.ImDecode(data, ImreadModes.Color);
        if (decoded.Empty() || decoded.Width > 1920 || decoded.Height > 1920 || (long)decoded.Width * decoded.Height > 1920 * 1080)
        { await Respond(tls, 400, "text/plain", [], ct); return; }
        Frames.Put(new Frame(decoded.Clone(), 1, Interlocked.Increment(ref _frameId), received));
        CaptureRate.Tick();
        await Respond(tls, 200, "text/plain", [], ct);
    }

    private static async Task<byte[]?> ReadChunked(Stream stream, CancellationToken ct)
    {
        using var result = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            var line = new List<byte>();
            while (line.Count < 128)
            {
                if (await stream.ReadAsync(one, ct) == 0) return null;
                line.Add(one[0]);
                if (line.Count >= 2 && line[^2] == 13 && line[^1] == 10) break;
            }
            if (line.Count < 2 || line[^2] != 13 || line[^1] != 10) return null;
            var sizeText = Encoding.ASCII.GetString(line.ToArray(), 0, line.Count - 2).Split(';')[0];
            if (!int.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int size) || size < 0 || result.Length + size > MaxImageBytes) return null;
            if (size == 0)
            {
                // クライアントはトレーラーを送らない。CRLF以外は受け付けない。
                var end = new byte[2]; await stream.ReadExactlyAsync(end, ct);
                return end[0] == 13 && end[1] == 10 ? result.ToArray() : null;
            }
            var chunk = new byte[size]; await stream.ReadExactlyAsync(chunk, ct);
            await result.WriteAsync(chunk, ct);
            var crlf = new byte[2]; await stream.ReadExactlyAsync(crlf, ct);
            if (crlf[0] != 13 || crlf[1] != 10) return null;
        }
    }

    // 展開前にJPEGのSOFを調べ、巨大な画像によるメモリ消費を防ぐ。
    private static bool ValidJpegSize(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8) return false;
        int p = 2;
        while (p + 4 <= data.Length)
        {
            if (data[p++] != 0xff) return false;
            while (p < data.Length && data[p] == 0xff) p++;
            if (p >= data.Length) return false;
            int marker = data[p++];
            if (marker is 0xd9 or 0xda) return false;
            if (marker == 0x01 || marker is >= 0xd0 and <= 0xd7) continue;
            if (p + 2 > data.Length) return false;
            int size = (data[p] << 8) | data[p + 1];
            if (size < 2 || p + size > data.Length) return false;
            if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
            {
                if (size < 8) return false;
                int height = (data[p + 3] << 8) | data[p + 4];
                int width = (data[p + 5] << 8) | data[p + 6];
                return width > 0 && height > 0 && width <= 1920 && height <= 1920 && (long)width * height <= 1920 * 1080;
            }
            p += size;
        }
        return false;
    }

    private static async Task Respond(Stream stream, int status, string contentType, byte[] body, CancellationToken ct)
    {
        string reason = status switch { 200 => "OK", 403 => "Forbidden", 404 => "Not Found", 413 => "Payload Too Large", _ => "Bad Request" };
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\nX-Content-Type-Options: nosniff\r\n\r\n");
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
    }

    private const string Page = """
<!doctype html><html lang="ja"><meta name="viewport" content="width=device-width,initial-scale=1"><meta charset="utf-8">
<title>WebcamFbt スマホカメラ</title><style>body{font:16px system-ui;background:#102334;color:#fff;margin:1.4em}video{width:100%;max-width:720px;background:#000}button{font-size:1.1em;padding:.7em;margin:.5em 0}p{line-height:1.6}</style>
<h1>スマホカメラ</h1><p>スマホを固定し、全身が入るようにしてください。撮影開始後はこの画面を開いたままにします。</p><video id="preview" autoplay playsinline muted></video><p><button id="start">カメラを開始</button> <button id="stop">停止</button></p><p id="status">待機中</p>
<script>
const token=location.hash.slice(1), v=document.querySelector('#preview'), statusEl=document.querySelector('#status'); let stream=null, running=false, generation=0;
document.querySelector('#start').onclick=async()=>{if(running)return; running=true;const id=++generation;try {if(!token)throw Error('PCに表示されたURLをもう一度開いてください'); statusEl.textContent='カメラを準備中';const next=await navigator.mediaDevices.getUserMedia({audio:false,video:{facingMode:'environment',width:{ideal:1280},height:{ideal:720},frameRate:{ideal:15,max:15}}});if(id!==generation){next.getTracks().forEach(t=>t.stop());return;}stream=next;v.srcObject=stream;await v.play();if(id!==generation)return;statusEl.textContent='Mac / PCに画像を送信しています…';send(id);}catch(e){if(id!==generation)return;running=false;statusEl.textContent='開始できません: '+e.message;if(stream)stream.getTracks().forEach(t=>t.stop());stream=null;}};
document.querySelector('#stop').onclick=()=>{running=false;generation++;if(stream)stream.getTracks().forEach(t=>t.stop());stream=null;statusEl.textContent='停止中';};
const canvas=document.createElement('canvas');async function send(id){let count=0,waitingSince=Date.now();while(running&&id===generation){try{if(v.videoWidth&&v.videoHeight){const w=v.videoWidth,h=v.videoHeight,scale=Math.min(1,1280/Math.max(w,h),720/Math.min(w,h));canvas.width=Math.max(1,Math.round(w*scale));canvas.height=Math.max(1,Math.round(h*scale));canvas.getContext('2d').drawImage(v,0,0,canvas.width,canvas.height);const blob=await new Promise(r=>canvas.toBlob(r,'image/jpeg',.7));if(!blob)throw Error('画像を作成できません');const abort=new AbortController(),timeout=setTimeout(()=>abort.abort(),8000);let response;try{response=await fetch('/frame',{method:'POST',headers:{'X-Camera-Token':token,'Content-Type':'image/jpeg'},body:blob,cache:'no-store',signal:abort.signal});}finally{clearTimeout(timeout);}if(!response.ok)throw Error('受信側のエラー '+response.status);if(id!==generation)return;count++;statusEl.textContent='送信中（受信確認 '+count+' 枚）';}else if(Date.now()-waitingSince>8000)throw Error('カメラ映像を取得できません');await new Promise(r=>setTimeout(r,60));}catch(e){if(id!==generation)return;statusEl.textContent='送信が停止しました: '+(e.name==='AbortError'?'受信側から応答がありません':e.message||e)+'。MacとiPhoneのWi-Fi・ファイアウォールを確認してください。';running=false;if(stream)stream.getTracks().forEach(t=>t.stop());}}}
</script></html>
""";

    public void Stop()
    {
        _stop?.Cancel();
        _listener?.Stop();
        try { _worker?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _listener = null; _worker = null;
        _stop?.Dispose(); _stop = null;
        _server?.Dispose(); _server = null;
        _root?.Dispose(); _root = null;
        if (CertificatePath.Length > 0) { try { File.Delete(CertificatePath); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        Frames.Clear(); CaptureRate.Reset();
    }

    public void Dispose() { Stop(); Frames.Dispose(); }
}
