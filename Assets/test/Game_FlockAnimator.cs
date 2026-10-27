using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 把飞行状态映射到 Animator。
/// 单 clip 模式：不切换动画片段，只用播放速度表达状态差异，因此不会有序列帧跳帧问题。
/// 只读 Agent，不回写。
/// </summary>
public class Game_FlockAnimator : MonoBehaviour, IGamePoolable
{
    // ==================== 引用 ====================
    [Header("引用")]
    public Animator animator;

    // ==================== 单 clip 模式 ====================
    [Header("单 clip 模式（推荐，不会跳帧）")]
    [Tooltip("唯一使用的动画状态名，必须与 Animator 里的状态名完全一致")]
    public string baseStateName = "Flap";
    [Tooltip("勾上 = 全程只播这一个 clip，状态只改变速度")]
    public bool singleClipMode = true;

    // ==================== 多 clip 模式（可选） ====================
    [Header("多 clip 模式（需保证各 clip 首帧姿态一致，否则会跳变）")]
    public string cruiseStateName = "Flap";
    public string glideStateName  = "Soar";
    public string dashStateName   = "Dive";
    public string restStateName   = "Idle";

    [Header("多 clip 模式：各状态基础倍率")]
    public float cruiseMul = 1.0f;
    public float glideMul  = 0.6f;
    public float dashMul   = 1.8f;
    public float restMul   = 0.4f;

    // ==================== 播放速度 ====================
    [Header("速度比例映射")]
    [Tooltip("实际速度为 0 时的倍率。别设 0，否则翅膀会停")]
    public float speedRatioMin = 0.55f;
    [Tooltip("实际速度达到 speedCeil 时的倍率")]
    public float speedRatioMax = 1.45f;
    [Tooltip("按 speedFast.y 填")]
    public float speedCeil = 4f;

    [Header("平滑（消除状态切换时的速度骤变）")]
    [Tooltip("半衰期（秒）。0.12 手感最好，0 = 不平滑")]
    public float speedSmoothing = 0.12f;

    [Header("起始帧错开")]
    [Tooltip("只在生成时随机一次。中途禁止重设，否则会跳帧")]
    public bool randomizeStartFrame = true;

    // ==================== 运行时 ====================
    private Game_FlockChild_Test _agent;
    private float _currentAnimSpeed = -1f;   // -1 = 尚未初始化
    private bool  _initialized;
    private bool  _culled;
    private bool  _needsPlay  = true;          // ← 新增：需要重新 Play（池化复用 / 剔除恢复）

    // ==================== 生命周期 ====================
    private void Awake()
    {
        if (animator == null) animator = GetComponentInChildren<Animator>();
        _agent = GetComponentInParent<Game_FlockChild_Test>();

        if (animator == null)
        {
            Debug.LogWarning($"[FlockAnimator] {name} 找不到 Animator，组件已禁用");
            enabled = false;
            return;
        }

        // ★ 关键校验：状态名对不上时 Animator.Play 会静默失败
        if (animator.runtimeAnimatorController != null &&
            animator.HasState(0, Animator.StringToHash(baseStateName)) == false)
        {
            var names = new List<string>();
            var states = animator.runtimeAnimatorController.animationClips;
            foreach (var c in states) names.Add(c.name);

            Debug.LogError(
                $"[FlockAnimator] Animator 里找不到状态 \"{baseStateName}\"！动画不会播放。" +
                $"当前 controller 的 clip: {string.Join(", ", names)}");
        }
    }

    private void Update()
    {
        if (animator == null || !animator.enabled) return;
        if (_agent == null) return;

        if (_needsPlay)
        {
            _needsPlay = false;
            string stateName = singleClipMode ? baseStateName : GetStateName(_agent);
            if (!string.IsNullOrEmpty(stateName))
            {
                if (randomizeStartFrame) animator.Play(stateName, 0, Random.value);
                else                     animator.Play(stateName, 0);
            }
        }

        float ratio = Mathf.Clamp01(_agent.CurrentSpeed / Mathf.Max(0.01f, speedCeil));
        float baseMul = singleClipMode ? cruiseMul : GetStateMul(_agent);
        float target = Mathf.Lerp(speedRatioMin, speedRatioMax, ratio) * baseMul * _agent.FlapBoost * _agent.RestBeat;

        if (_currentAnimSpeed < 0f) _currentAnimSpeed = target;
        else if (speedSmoothing > 0f)
        {
            float t = 1f - Mathf.Exp(-Mathf.Log(2f) * Time.deltaTime / speedSmoothing);
            _currentAnimSpeed = Mathf.Lerp(_currentAnimSpeed, target, t);
        }
        else _currentAnimSpeed = target;

        animator.speed = _currentAnimSpeed;
    }

    public void OnSpawned()
    {
        _currentAnimSpeed = -1f;
        _needsPlay        = true;
        if (animator != null) animator.enabled = true;
    }

    public void OnDespawned() { }

    // ==================== 多 clip 模式辅助 ====================
    private float GetStateMul(Game_FlockChild_Test a)
    {
        if (a.IsResting) return restMul;
        return a.State switch
        {
            Game_FlockChild_Test.FlyState.Glide => glideMul,
            Game_FlockChild_Test.FlyState.Dash => dashMul,
            _ => cruiseMul
        };
    }

    private string GetStateName(Game_FlockChild_Test a)
    {
        if (a.IsResting) return restStateName;
        switch (a.State)
        {
            case Game_FlockChild_Test.FlyState.Glide: return glideStateName;
            case Game_FlockChild_Test.FlyState.Dash:  return dashStateName;
            default:                                  return cruiseStateName;
        }
    }
}