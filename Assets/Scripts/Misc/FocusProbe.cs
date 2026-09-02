using System;
using System.Runtime.InteropServices;
using MajdataViewX.Managers;
using MajdataViewX.Types.Enums;
using UnityEngine;

/// <summary>
/// 帧率调度器：在不需要高帧率的场景下降低帧率以省 CPU，需要时恢复。
///
/// 触发降帧（降到 ThrottledFrameRate）的两个条件，满足其一即可：
///   1. 宿主窗口被最小化（IsIconic 判定，缓存父链顶层窗口句柄）
///   2. 处于默认界面（ViewStatus.Idle，无谱面）
/// 其余状态（前台 / 被遮挡但已载入谱面）恢复原帧率。
/// 录制中（Time.captureFramerate != 0）永不降帧，避免破坏固定帧率捕获。
///
/// 窗口句柄通过 GetActiveWindow + GetAncestor(GA_ROOT) 获取父链顶层窗口，
/// 并在窗口激活期间持续采纳最新值。
/// 注意不能每帧从 GetActiveWindow 重取来实时判定：
/// 窗口最小化后会失去激活，GetActiveWindow 返回 0。
/// 因此改用缓存句柄，最小化期间 IsIconic 保持 true。
/// </summary>
public class FocusProbe : MonoBehaviour
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    private const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    // 降帧目标帧率。
    // 不可过低：帧率过低会拉长主循环间隔，导致从最小化恢复或响应 HTTP 指令时出现明显延迟。
    private const int ThrottledFrameRate = 50;

    // 锁帧状态：进入降帧时保存原值，退出时恢复。
    private bool _isThrottling;
    private int _savedTargetFrameRate;
    private int _savedVSyncCount;

    // 父链顶层窗口句柄。窗口激活时持续采纳最新值以自我修复；
    // 最小化后 GetActiveWindow 返回 0，此时保留上次缓存值继续判定。
    private IntPtr _rootHandle;

    private void Update()
    {
        var act = GetActiveWindow();
        if (act != IntPtr.Zero)
        {
            var root = GetAncestor(act, GA_ROOT);
            if (root != IntPtr.Zero) _rootHandle = root;
        }

        bool minimized = _rootHandle != IntPtr.Zero && IsIconic(_rootHandle);

        ApplyGovernor(minimized, IsIdle());
    }

    /// <summary>
    /// 根据降帧条件切换 targetFrameRate / vSyncCount（幂等）。
    /// 降帧时必须同时把 vSyncCount 置 0，否则 targetFrameRate 会被垂直同步忽略。
    /// </summary>
    private void ApplyGovernor(bool minimized, bool idle)
    {
        bool recording = Time.captureFramerate != 0;
        bool shouldThrottle = !recording && (minimized || idle);

        if (shouldThrottle == _isThrottling) return;

        if (shouldThrottle)
        {
            _savedTargetFrameRate = Application.targetFrameRate;
            _savedVSyncCount = QualitySettings.vSyncCount;
            Application.targetFrameRate = ThrottledFrameRate;
            QualitySettings.vSyncCount = 0;
        }
        else
        {
            Application.targetFrameRate = _savedTargetFrameRate;
            QualitySettings.vSyncCount = _savedVSyncCount;
        }
        _isThrottling = shouldThrottle;
    }

    /// <summary>是否处于默认界面（无谱面）。</summary>
    private static bool IsIdle() => PlayManager.State == ViewStatus.Idle;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoCreate()
    {
        var go = new GameObject("__FocusProbe");
        go.AddComponent<FocusProbe>();
        DontDestroyOnLoad(go);
    }
}
