using System.Runtime.InteropServices;

namespace WebcamFbt.Mac.Platform;

/// <summary>
/// 実行中に Mac がスリープしたり、App Nap で減速したりしないよう macOS に頼む
/// （NSProcessInfo の beginActivityWithOptions:reason: / endActivity:）。
/// Quest で遊んでいる間は Mac を触らないので、これが無いと「一定時間操作しないとスリープ」で止まる。
/// 画面が消えるのは止めない（処理は続くので省電力のため）。Mac 以外では何もしない。
/// UI スレッドから呼ぶこと。
/// </summary>
public sealed class SleepBlocker
{
    // NSActivityOptions（Foundation/NSProcessInfo.h）
    // UserInitiated = 0x00FFFFFF | IdleSystemSleepDisabled(1 << 20): App Nap を止め、操作が無くてもスリープしない
    // LatencyCritical = 0xFF00000000: タイマーなどの遅延を許さない（カメラ・OSC の周期を保つ）
    private const ulong NSActivityUserInitiated = 0x00FFFFFFUL | (1UL << 20);
    private const ulong NSActivityLatencyCritical = 0xFF00000000UL;

    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    private IntPtr _token;

    public bool IsActive => _token != IntPtr.Zero;

    /// <summary>スリープ防止を始める。既に始めていれば何もしない。失敗してもアプリは止めない。</summary>
    public void Begin(string reason)
    {
        if (!OperatingSystem.IsMacOS() || _token != IntPtr.Zero) return;
        try
        {
            var processInfo = MsgSend(objc_getClass("NSProcessInfo"), sel_registerName("processInfo"));
            var nsReason = MsgSend(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), reason);
            var token = MsgSend(processInfo, sel_registerName("beginActivityWithOptions:reason:"),
                NSActivityUserInitiated | NSActivityLatencyCritical, nsReason);
            if (token == IntPtr.Zero) return;
            // 戻り値は autorelease されるので、終わるまで保持する
            _token = MsgSend(token, sel_registerName("retain"));
        }
        catch (Exception ex)
        {
            CrashLog.Write($"スリープ防止を始められませんでした: {ex}");
            _token = IntPtr.Zero;
        }
    }

    /// <summary>スリープ防止をやめる。</summary>
    public void End()
    {
        if (_token == IntPtr.Zero) return;
        try
        {
            var processInfo = MsgSend(objc_getClass("NSProcessInfo"), sel_registerName("processInfo"));
            MsgSendVoid(processInfo, sel_registerName("endActivity:"), _token);
            MsgSendVoid(_token, sel_registerName("release"));
        }
        catch (Exception ex)
        {
            CrashLog.Write($"スリープ防止を終えられませんでした: {ex}");
        }
        _token = IntPtr.Zero;
    }

    // ---- Objective-C ランタイム（objc_msgSend は呼び出しごとに引数の型を固定して宣言する） ----

    [DllImport(ObjC)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, ulong options, IntPtr reason);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoid(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoid(IntPtr receiver, IntPtr selector);
}
