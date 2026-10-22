using System.Buffers.Binary;
using System.Text;

namespace WebcamFbt.Osc;

/// <summary>
/// OSC 1.0 のメッセージをバイト列にする（このアプリで必要な float 引数のみ対応）。
///
/// 形式: アドレス文字列 → 型タグ文字列（",fff" など） → 引数
///  - 文字列は ASCII + 終端 NUL で、全体を 4 バイト単位になるよう NUL で埋める
///  - float は 32bit のビッグエンディアン
/// </summary>
public static class OscMessage
{
    /// <summary>float 引数を持つメッセージを作る。</summary>
    public static byte[] Encode(string address, params ReadOnlySpan<float> args)
    {
        if (string.IsNullOrEmpty(address) || address[0] != '/')
        {
            throw new ArgumentException("OSC アドレスは '/' で始まる必要があります。", nameof(address));
        }

        int addressLen = PaddedLength(Encoding.ASCII.GetByteCount(address));
        int tagLen = PaddedLength(1 + args.Length); // ',' + 'f' × 個数
        var buffer = new byte[addressLen + tagLen + 4 * args.Length];

        int offset = 0;
        Encoding.ASCII.GetBytes(address, buffer.AsSpan(offset));
        offset += addressLen;

        buffer[offset] = (byte)',';
        for (int i = 0; i < args.Length; i++) buffer[offset + 1 + i] = (byte)'f';
        offset += tagLen;

        foreach (var value in args)
        {
            BinaryPrimitives.WriteSingleBigEndian(buffer.AsSpan(offset, 4), value);
            offset += 4;
        }
        return buffer;
    }

    /// <summary>終端 NUL を含めて 4 の倍数に切り上げた長さ。</summary>
    internal static int PaddedLength(int byteCount) => (byteCount + 4) & ~3;
}
