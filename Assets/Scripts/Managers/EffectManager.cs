#nullable enable

using MajdataViewX.Base;
using MajdataViewX.Types.Enums;
using MajdataViewX.Types.Input;
using MajdataViewX.Types.Notes;
using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

using static MajdataViewX.Base.MajCtx;

namespace MajdataViewX.Managers
{
    public class EffectManager : MonoBehaviour
    {
        public const int EFFECT_COUNT = BUTTON_COUNT + SENSOR_COUNT;

        private static readonly int PerfectHash = Animator.StringToHash("perfect");
        private static readonly int GreatHash = Animator.StringToHash("great");
        private static readonly int GoodHash = Animator.StringToHash("good");
        private static readonly int BPerfectHash = Animator.StringToHash("bPerfect");
        private static readonly int BGreatHash = Animator.StringToHash("bGreat");
        private static readonly int BGoodHash = Animator.StringToHash("bGood");
        private static readonly int FireHash = Animator.StringToHash("fire");
        // Play() 收的是状态全路径哈希（Hanabi 控制器只有一个 Base Layer）
        private static readonly int FireStateHash = Animator.StringToHash("Base Layer.Fire");
        private static readonly int FireIdleStateHash = Animator.StringToHash("Base Layer.FireIdle");

        [SerializeField]
        GameObject effectPrefab;

        public NativeArray<EffectData> judgeEffectRequests = new(EFFECT_COUNT, Allocator.Persistent);
        public unsafe EffectData* JudgeEffectRequestsPtr => (EffectData*)judgeEffectRequests.GetUnsafePtr();

        private readonly Animator[] tapAnimators = new Animator[EFFECT_COUNT];

        private readonly GameObject[] holdEffects = new GameObject[EFFECT_COUNT];
        private readonly Material[] holdMaterials = new Material[EFFECT_COUNT];

        private readonly GameObject[] touchEffects = new GameObject[EFFECT_COUNT];
        private readonly Animator[] touchAnimators = new Animator[EFFECT_COUNT];

        private readonly Animator[] judgeAnimators = new Animator[EFFECT_COUNT];
        private readonly SpriteRenderer[] judgeRenderers = new SpriteRenderer[EFFECT_COUNT];

        private readonly SpriteRenderer[] fastLateRenderers = new SpriteRenderer[EFFECT_COUNT];

        private GameObject fireworkEffect;
        private Animator fireworkAnimator;
        private float fireworkDurationSec = 1.3333334f; // Fire 剪辑长度兜底值
        private int derivedFireworkIndex = -1;
        private float derivedFireworkTime;
        private bool derivedFireworkActive;

        private void Awake()
        {
            _effectManager = this;
        }

        private void Start()
        {
            var parent = GameObject.Find("NoteEffects");

            for (var i = 0; i < EFFECT_COUNT; i++)
            {
                float2 pos;
                if (i < BUTTON_COUNT)
                {
                    pos = MajPos.GetBtnPos(i);
                }
                else
                {
                    pos = MajPos.GetAreaPos((SensorType)i - 8);
                }
                float ang = 0f;
                if (i - 8 < 16)       // 1~8, A1~B8
                    ang = -45f * (i % 8) - 22.5f;
                else if (i - 8 > 16)  // D1~E8
                    ang = -45f * (i % 8 - 1) - 22.5f;

                var effect = Instantiate(
                    effectPrefab,
                    new Vector3(pos.x, pos.y, 0),
                    Quaternion.Euler(new Vector3(0, 0, ang)),
                    parent.transform);

                var tapEffect = effect.transform.GetChild(0).gameObject;
                tapAnimators[i] = tapEffect.GetComponent<Animator>();
                if (i > 7) tapEffect.SetActive(false); // touch 部分的不要了 

                holdEffects[i] = effect.transform.GetChild(1).gameObject;
                holdMaterials[i] = holdEffects[i].GetComponent<ParticleSystemRenderer>().material;
                holdEffects[i].SetActive(false);

                touchEffects[i] = effect.transform.GetChild(2).gameObject;
                touchEffects[i].transform.localEulerAngles = new Vector3(0, 0, -ang); // 回正
                touchAnimators[i] = touchEffects[i].GetComponent<Animator>();
                if (i <= 7) touchEffects[i].SetActive(false); // tap 部分的不要了 

                var judgeEffect = effect.transform.GetChild(3).gameObject;
                judgeAnimators[i] = judgeEffect.GetComponent<Animator>();
                judgeRenderers[i] = judgeEffect.transform.GetChild(0).GetChild(0).gameObject.GetComponent<SpriteRenderer>();
                judgeEffect.transform.GetChild(0).GetChild(1).gameObject.GetComponent<SpriteRenderer>().sprite = _noteSkinManager.JudgeText_BPerfect;
                fastLateRenderers[i] = judgeEffect.transform.GetChild(1).GetChild(0).GetComponent<SpriteRenderer>();

                fireworkEffect = GameObject.Find("FireworkEffect");
                fireworkAnimator = fireworkEffect.GetComponent<Animator>();
            }

            foreach (var clip in fireworkAnimator.runtimeAnimatorController.animationClips)
                if (clip.name == "Fire")
                {
                    fireworkDurationSec = clip.length;
                    break;
                }
        }

        private void OnDestroy()
        {
            if (judgeEffectRequests.IsCreated) judgeEffectRequests.Dispose();
        }

        public void ProcessEffectRequests()
        {
            for (var i = 0; i < judgeEffectRequests.Length; i++)
            {
                var req = judgeEffectRequests[i];

                if (!req.IsMine ||
                    (req.IsMine && req.JudgeGrade is (JudgeGrade.Miss or JudgeGrade.TooFast)))
                {
                    if (req.Effect.HasFlag(EffectType.Tap))
                    {
                        PlayTapEffect(i, req.JudgeGrade, req.IsBreak);
                    }
                    if (req.Effect.HasFlag(EffectType.Touch))
                    {
                        PlayTouchEffect(i, req.JudgeGrade, req.IsBreak);
                    }
                    if (req.Effect.HasFlag(EffectType.Firework))
                    {
                        PlayFireworkEffect(i);
                    }
                }

                holdEffects[i].SetActive(req.HasHolding);
                if (req.HasHolding)
                {
                    holdMaterials[i].SetColor("_Color", req.HoldingColor);
                }
            }

            for (var i = 0; i < judgeEffectRequests.Length; i++)
                judgeEffectRequests[i] = default;

            UpdateDerivedFirework();
        }

        /// <summary>暂停/静载/回溯时按 NoteTime 反推烟花相位，与 note 的 SeeOnly 渲染同域。</summary>
        private void UpdateDerivedFirework()
        {
            if (_timeProvider.IsStart ||
                fireworkAnimator == null ||
                PlayManager.State is not (ViewStatus.Loaded or ViewStatus.Paused))
            {
                ReleaseDerivedFirework();
                return;
            }

            var noteTime = _timeProvider.NoteTime;
            var index = FindFireworkIndexAt(_noteManager.Fireworks, noteTime, fireworkDurationSec);

            if (!derivedFireworkActive)
            {
                derivedFireworkActive = true;
                derivedFireworkIndex = -1;
                derivedFireworkTime = float.NaN;
                fireworkAnimator.speed = 0f;
                // 暂停瞬间可能挂着一个尚未消费的 trigger，冻结时不能把它漏出去
                fireworkAnimator.ResetTrigger(FireHash);
            }

            if (index == derivedFireworkIndex && noteTime == derivedFireworkTime) return;

            derivedFireworkIndex = index;
            derivedFireworkTime = noteTime;
            if (index < 0)
            {
                // 窗口内没有烟花：停在不可见的静止态，保持冻结
                fireworkAnimator.Play(FireIdleStateHash, 0, 0f);
            }
            else
            {
                var ev = _noteManager.Fireworks[index];
                SetFireworkPosition((int)ev.sensor + BUTTON_COUNT);
                fireworkAnimator.Play(FireStateHash, 0, (noteTime - ev.time) / fireworkDurationSec);
            }
            fireworkAnimator.Update(0f); // Play 只排队，不手动求值则本帧仍是旧姿势
        }

        /// <summary>最后一个 time &lt;= noteTime 且仍在 Fire 窗口内的事件；更早的都已放完。</summary>
        private static int FindFireworkIndexAt(NativeArray<FireworkEvent> events, float noteTime, float durationSec)
        {
            int lo = 0, hi = events.Length - 1, found = -1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                if (events[mid].time <= noteTime)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else hi = mid - 1;
            }
            if (found < 0 || noteTime - events[found].time >= durationSec) return -1;
            return found;
        }

        private void ReleaseDerivedFirework()
        {
            if (!derivedFireworkActive) return;
            derivedFireworkActive = false;
            derivedFireworkIndex = -1;
            derivedFireworkTime = 0f;
            // 恢复原速：动画从中途接着播，正好接上冻结时的相位
            fireworkAnimator.speed = 1f;
        }

        private void PlayTapEffect(int pos, JudgeGrade judge, bool isBreak)
        {
            // Effect & Judge Text
            switch (judge)
            {
                case JudgeGrade.LateGood:
                case JudgeGrade.FastGood:
                    judgeRenderers[pos].sprite = _noteSkinManager.JudgeText[1];
                    if (isBreak)
                    {
                        tapAnimators[pos].speed = 0.9f;
                        tapAnimators[pos].SetTrigger(BGoodHash);
                    }
                    else
                    {
                        tapAnimators[pos].speed = 1f;
                        tapAnimators[pos].SetTrigger(GoodHash);
                    }
                    break;
                case JudgeGrade.LateGreat3rd:
                case JudgeGrade.LateGreat2nd:
                case JudgeGrade.LateGreat1st:
                case JudgeGrade.FastGreat3rd:
                case JudgeGrade.FastGreat2nd:
                case JudgeGrade.FastGreat1st:
                    judgeRenderers[pos].sprite = _noteSkinManager.JudgeText[2];
                    if (isBreak)
                    {
                        tapAnimators[pos].speed = 0.9f;
                        tapAnimators[pos].SetTrigger(BGreatHash);
                    }
                    else
                    {
                        tapAnimators[pos].speed = 1f;
                        tapAnimators[pos].SetTrigger(GreatHash);
                    }
                    break;
                case JudgeGrade.LatePerfect3rd:
                case JudgeGrade.LatePerfect2nd:
                case JudgeGrade.FastPerfect3rd:
                case JudgeGrade.FastPerfect2nd:
                    judgeRenderers[pos].sprite = _noteSkinManager.JudgeText[3];
                    if (isBreak)
                    {
                        tapAnimators[pos].speed = 0.9f;
                        tapAnimators[pos].SetTrigger(BPerfectHash);
                    }
                    else
                    {
                        tapAnimators[pos].speed = 1f;
                        tapAnimators[pos].SetTrigger(PerfectHash);
                    }
                    break;
                case JudgeGrade.LateCritical:
                case JudgeGrade.FastCritical:
                    judgeRenderers[pos].sprite = _noteSkinManager.JudgeText[4];
                    if (isBreak)
                    {
                        tapAnimators[pos].speed = 0.9f;
                        tapAnimators[pos].SetTrigger(BPerfectHash);
                    }
                    else
                    {
                        tapAnimators[pos].speed = 1f;
                        tapAnimators[pos].SetTrigger(PerfectHash);
                    }
                    break;
                default:
                    judgeRenderers[pos].sprite = _noteSkinManager.JudgeText[0];
                    break;
            }

            // Judge Anim
            if (isBreak && (judge is JudgeGrade.LateCritical or JudgeGrade.FastCritical))
                judgeAnimators[pos].SetTrigger(BPerfectHash);
            else
                judgeAnimators[pos].SetTrigger(PerfectHash);

            // Fast / Late
            if (judge is JudgeGrade.Miss or JudgeGrade.LateCritical or JudgeGrade.FastCritical)
            {
                fastLateRenderers[pos].sprite = null;
            }
            else
            {
                var isFast = judge <= JudgeGrade.FastCritical;
                if (isFast)
                    fastLateRenderers[pos].sprite = _noteSkinManager.FastText;
                else
                    fastLateRenderers[pos].sprite = _noteSkinManager.LateText;
            }
        }

        private void PlayTouchEffect(int pos, JudgeGrade judge, bool isBreak)
        {
            // Effect & Judge Text
            switch (judge)
            {
                case JudgeGrade.LateGood:
                case JudgeGrade.FastGood:
                    judgeRenderers[pos].sprite = _noteSkinManager.JudgeText[1];
                    touchAnimators[pos].SetTrigger(GoodHash);
                    break;
                case JudgeGrade.LateGreat3rd:
                case JudgeGrade.LateGreat2nd:
                case JudgeGrade.LateGreat1st:
                case JudgeGrade.FastGreat3rd:
                case JudgeGrade.FastGreat2nd:
                case JudgeGrade.FastGreat1st:
                    judgeRenderers[pos].sprite = _noteSkinManager.JudgeText[2];
                    touchAnimators[pos].SetTrigger(GreatHash);
                    break;
                case JudgeGrade.LatePerfect3rd:
                case JudgeGrade.LatePerfect2nd:
                case JudgeGrade.FastPerfect3rd:
                case JudgeGrade.FastPerfect2nd:
                    judgeRenderers[pos].sprite = _noteSkinManager.JudgeText[3];
                    touchAnimators[pos].SetTrigger(PerfectHash);
                    break;
                case JudgeGrade.LateCritical:
                case JudgeGrade.FastCritical:
                    judgeRenderers[pos].sprite = _noteSkinManager.JudgeText[4];
                    touchAnimators[pos].SetTrigger(PerfectHash);
                    break;
                default:
                    judgeRenderers[pos].sprite = _noteSkinManager.JudgeText[0];
                    break;
            }

            // Judge Anim
            if (isBreak && (judge is JudgeGrade.LateCritical or JudgeGrade.FastCritical))
                judgeAnimators[pos].SetTrigger(BPerfectHash);
            else
                judgeAnimators[pos].SetTrigger(PerfectHash);

            // Fast / Late
            if (judge is JudgeGrade.Miss or JudgeGrade.LateCritical or JudgeGrade.FastCritical)
            {
                fastLateRenderers[pos].sprite = null;
            }
            else
            {
                var isFast = judge <= JudgeGrade.FastCritical;
                if (isFast)
                    fastLateRenderers[pos].sprite = _noteSkinManager.FastText;
                else
                    fastLateRenderers[pos].sprite = _noteSkinManager.LateText;
            }
        }

        public void PlayFireworkEffect(int pos)
        {
            SetFireworkPosition(pos);
            fireworkAnimator.SetTrigger(FireHash);
        }

        private void SetFireworkPosition(int pos)
        {
            float2 worldPos;
            if (pos is < 0 or > EFFECT_COUNT) return;
            else if (pos < BUTTON_COUNT) worldPos = MajPos.GetBtnPos(pos);
            else worldPos = MajPos.GetAreaPos((SensorType)(pos - 8));
            fireworkEffect.transform.position = new float3(worldPos, 0);
        }

        public void ResetState()
        {
            for (var i = 0; i < judgeEffectRequests.Length; i++)
                judgeEffectRequests[i] = default;

            derivedFireworkIndex = -1;
            derivedFireworkTime = 0f;
            derivedFireworkActive = false;
            if (fireworkAnimator == null) return; // Start 之前就收到 Reset
            fireworkAnimator.speed = 1f;
            fireworkAnimator.ResetTrigger(FireHash);
            fireworkAnimator.Play(FireIdleStateHash, 0, 0f);
            fireworkAnimator.Update(0f);
        }
    }

    public struct EffectData
    {
        public EffectType Effect;
        public JudgeGrade JudgeGrade;
        public bool IsBreak;
        public bool IsMine;
        public bool HasHolding;
        public Color HoldingColor;
    }

    [Flags]
    public enum EffectType
    {
        None = 0,
        Tap = 1 << 0,
        Touch = 1 << 1,
        Firework = 1 << 2
    }
}