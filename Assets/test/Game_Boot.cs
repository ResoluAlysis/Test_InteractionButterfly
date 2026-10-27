using UnityEngine;

/// <summary>
/// 最早的启动钩子：在 Splash Screen 之前就把帧率锁住。
///
/// 不依赖任何场景对象（Game_Config 在第一个场景加载后才 Awake，
/// 那时 Splash 已经跑完了，帧率在那几秒是完全不受限的）。
/// </summary>
public static class Game_Boot
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
    private static void ApplyEarlyFrameLimit()
    {
        // Splash 阶段是独占全屏，vSync 可靠 → 先靠它压住
        QualitySettings.vSyncCount = 1;

        // 同时给一个硬上限，防止高刷屏（144/165Hz）跑满
        Application.targetFrameRate = 60;
    }
}