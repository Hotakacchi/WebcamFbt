using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using OpenCvSharp;
using WebcamFbt.Vision.Camera;

namespace WebcamFbt.Core.Tests;

public class PhoneCameraTests
{
    [Fact]
    public async Task Httpsでページを取得し認証済みの画像だけを受信でき停止時に閉じる()
    {
        using var camera = new PhoneCameraSource(0, IPAddress.Loopback);
        string? serverError = null;
        camera.Error += message => serverError = message;
        camera.Start();
        var uri = new Uri(camera.ConnectionUrl);
        var token = uri.Fragment.TrimStart('#');
        var baseUri = new UriBuilder(uri) { Fragment = "" }.Uri;
        using var root = X509CertificateLoader.LoadCertificateFromFile(camera.CertificatePath);
        using var handler = new HttpClientHandler { UseProxy = false };
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null || (errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(certificate);
        };
        using var client = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(5) };
        // Safariなどが先に接続だけ開いても、次のHTTPS通信を妨げない。
        using var idle = new TcpClient();
        await idle.ConnectAsync(IPAddress.Loopback, uri.Port);
        string page;
        try { page = await client.GetStringAsync("/"); }
        catch (HttpRequestException ex) { throw new Exception($"サーバー: {serverError}", ex); }
        Assert.Contains("スマホカメラ", page);
        using var mat = new Mat(32, 48, MatType.CV_8UC3, new Scalar(1, 2, 3));
        var jpeg = mat.ImEncode(".jpg");
        using var invalid = new HttpRequestMessage(HttpMethod.Post, "/frame") { Content = new ByteArrayContent(jpeg) };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(invalid)).StatusCode);
        using var invalidImage = new HttpRequestMessage(HttpMethod.Post, "/frame") { Content = new ByteArrayContent([1, 2, 3]) };
        invalidImage.Headers.Add("X-Camera-Token", token);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(invalidImage)).StatusCode);
        Assert.Null(camera.Frames.TryTake());
        using var valid = new HttpRequestMessage(HttpMethod.Post, "/frame") { Content = new ByteArrayContent(jpeg) };
        valid.Headers.Add("X-Camera-Token", token);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(valid)).StatusCode);
        using var frame = camera.Frames.Take(TimeSpan.FromSeconds(1));
        Assert.NotNull(frame);
        Assert.Equal(48, frame!.Image.Width);
        Assert.Equal(32, frame.Image.Height);
        Assert.Equal(1, frame.CameraIndex);
        using var chunked = new HttpRequestMessage(HttpMethod.Post, "/frame") { Content = new ByteArrayContent(jpeg) };
        chunked.Headers.Add("X-Camera-Token", token);
        chunked.Headers.TransferEncodingChunked = true;
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(chunked)).StatusCode);
        using var chunkedFrame = camera.Frames.Take(TimeSpan.FromSeconds(1));
        Assert.NotNull(chunkedFrame);
        camera.Stop();
        Assert.False(File.Exists(camera.CertificatePath));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync("/"));
    }

    [Fact]
    public void 不特定アドレスへの公開は禁止する()
    {
        using var camera = new PhoneCameraSource(0, IPAddress.Any);
        Assert.Throws<InvalidOperationException>(() => camera.Start());
    }
}
