
using MajdataViewX.Managers;
using MajdataViewX.Types.Enums;
using MajdataViewX.Types.Input;
using MajdataViewX.Types.Notes;
using MajSimai;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using static MajdataViewX.Base.MajCtx;

namespace MajdataViewX.Notes
{
    public struct NoteSettings
    {
        public AutoPlayMode AutoPlayMode;
        public float TapSpeed;
        public float TouchSpeed;
        public bool LegacySlideLayer;
        public bool SmoothSlideAnime;
        public bool MineAutoSlide;
    }
    [BurstCompile]
    public static unsafe class NoteHelper
    {
        public static readonly SharedStatic<NoteSettings> NoteSettingsSS =
            SharedStatic<NoteSettings>.GetOrCreate<NoteSettings>();
        public static NoteSettings Settings =>
            NoteSettingsSS.Data;

        public static bool IsSimulated => Settings.AutoPlayMode is
            AutoPlayMode.DJAutoSensor or AutoPlayMode.DJAutoButton or AutoPlayMode.Disable;

        // ---- Judgment constants ----
        public const float TAP_JUDGE_SEG_1ST_PERFECT_MSEC = 1 * FRAME_LENGTH_MSEC;
        public const float TAP_JUDGE_SEG_2ND_PERFECT_MSEC = 2 * FRAME_LENGTH_MSEC;
        public const float TAP_JUDGE_SEG_3RD_PERFECT_MSEC = 3 * FRAME_LENGTH_MSEC;
        public const float TAP_JUDGE_SEG_1ST_GREAT_MSEC = 4 * FRAME_LENGTH_MSEC;
        public const float TAP_JUDGE_SEG_2ND_GREAT_MSEC = 5 * FRAME_LENGTH_MSEC;
        public const float TAP_JUDGE_SEG_3RD_GREAT_MSEC = 6 * FRAME_LENGTH_MSEC;
        public const float TAP_JUDGE_GOOD_AREA_MSEC = 9 * FRAME_LENGTH_MSEC;

        public const float HOLD_HEAD_IGNORE_LENGTH_SEC = 6 * FRAME_LENGTH_SEC;
        public const float HOLD_TAIL_IGNORE_LENGTH_SEC = 12 * FRAME_LENGTH_SEC;
        public const float DELUXE_HOLD_RELEASE_IGNORE_TIME_SEC = 2 * FRAME_LENGTH_SEC;

        public const float SLIDE_CHECK_AHEAD_TIME_MSEC = 6 * FRAME_LENGTH_MSEC;
        public const float SLIDE_FORCE_MISS = 33 * FRAME_LENGTH_MSEC;

        /// <summary>
        /// slide 第二段（星星沿轨迹移动）整体提前 60fps 一帧，仅影响渲染
        /// </summary>
        public const float SLIDE_VISUAL_LEAD_SEC = FRAME_LENGTH_SEC;

        public const float TOUCH_JUDGE_SEG_1ST_PERFECT_MSEC = 9 * FRAME_LENGTH_MSEC;
        public const float TOUCH_JUDGE_SEG_2ND_PERFECT_MSEC = 10.5f * FRAME_LENGTH_MSEC;
        public const float TOUCH_JUDGE_SEG_3RD_PERFECT_MSEC = 12 * FRAME_LENGTH_MSEC;
        public const float TOUCH_JUDGE_SEG_1ST_GREAT_MSEC = 13 * FRAME_LENGTH_MSEC;
        public const float TOUCH_JUDGE_SEG_2ND_GREAT_MSEC = 14 * FRAME_LENGTH_MSEC;
        public const float TOUCH_JUDGE_SEG_3RD_GREAT_MSEC = 15 * FRAME_LENGTH_MSEC;
        public const float TOUCH_JUDGE_GOOD_AREA_MSEC = 18 * FRAME_LENGTH_MSEC;

        public const float TOUCH_HOLD_HEAD_IGNORE_LENGTH_SEC = 15 * FRAME_LENGTH_SEC;
        public const float TOUCH_HOLD_TAIL_IGNORE_LENGTH_SEC = 12 * FRAME_LENGTH_SEC;

        public const float MINE_END_SEC = 3 * FRAME_LENGTH_SEC;




        // ============== Pure Math ==============

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [BurstCompile]
        public static void GetPosFromDistance(float distance, SensorType key, out float2 result)
        {
            int index = (int)key;
            if (index >= 0 && index < 8)
            {
                float2 dir = index switch
                {
                    0 => new(0.38268343f, 0.92387953f),
                    1 => new(0.92387953f, 0.38268343f),
                    2 => new(0.92387953f, -0.38268343f),
                    3 => new(0.38268343f, -0.92387953f),
                    4 => new(-0.38268343f, -0.92387953f),
                    5 => new(-0.92387953f, -0.38268343f),
                    6 => new(-0.92387953f, 0.38268343f),
                    7 => new(-0.38268343f, 0.92387953f),
                    _ => new(0, 0)
                };
                result = dir * distance;
                return;
            }
            result = float2.zero;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [BurstCompile]
        public static JudgeGrade GetTapJudge(float diffSec, bool isEx)
        {
            var isFast = diffSec < 0;
            var diffMSec = math.abs(diffSec * 1000);
            var result = diffMSec switch
            {
                <= TAP_JUDGE_SEG_1ST_PERFECT_MSEC => isFast ? JudgeGrade.FastCritical : JudgeGrade.LateCritical,
                <= TAP_JUDGE_SEG_2ND_PERFECT_MSEC => isFast ? JudgeGrade.FastPerfect2nd : JudgeGrade.LatePerfect2nd,
                <= TAP_JUDGE_SEG_3RD_PERFECT_MSEC => isFast ? JudgeGrade.FastPerfect3rd : JudgeGrade.LatePerfect3rd,
                <= TAP_JUDGE_SEG_1ST_GREAT_MSEC => isFast ? JudgeGrade.FastGreat1st : JudgeGrade.LateGreat1st,
                <= TAP_JUDGE_SEG_2ND_GREAT_MSEC => isFast ? JudgeGrade.FastGreat2nd : JudgeGrade.LateGreat2nd,
                <= TAP_JUDGE_SEG_3RD_GREAT_MSEC => isFast ? JudgeGrade.FastGreat3rd : JudgeGrade.LateGreat3rd,
                _ => isFast ? JudgeGrade.FastGood : JudgeGrade.LateGood
            };

            if (isEx) result = isFast ? JudgeGrade.FastCritical : JudgeGrade.LateCritical;
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [BurstCompile]
        public static JudgeGrade GetTouchJudge(float diffSec)
        {
            var diffMSec = diffSec * 1000;
            return diffMSec < 0 ? JudgeGrade.FastCritical
                : diffMSec <= TOUCH_JUDGE_SEG_1ST_PERFECT_MSEC ? JudgeGrade.LateCritical
                : diffMSec <= TOUCH_JUDGE_SEG_2ND_PERFECT_MSEC ? JudgeGrade.LatePerfect2nd
                : diffMSec <= TOUCH_JUDGE_SEG_3RD_PERFECT_MSEC ? JudgeGrade.LatePerfect3rd
                : diffMSec <= TOUCH_JUDGE_SEG_1ST_GREAT_MSEC ? JudgeGrade.LateGreat1st
                : diffMSec <= TOUCH_JUDGE_SEG_2ND_GREAT_MSEC ? JudgeGrade.LateGreat2nd
                : diffMSec <= TOUCH_JUDGE_SEG_3RD_GREAT_MSEC ? JudgeGrade.LateGreat3rd
                : JudgeGrade.LateGood;
        }

        /// <summary>
        /// Adjusts a hold/touch-hold head judgement by how long the body was actually held.
        /// Port of the legacy HoldDrop/TouchHoldDrop OnDestroy percent->grade mapping.
        /// <para><paramref name="percent"/> is the held ratio in [0,1]; <paramref name="realityHT"/>
        /// is the effective hold length (already excluding head/tail ignore windows).</para>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [BurstCompile]
        public static JudgeGrade GetHoldFinalGrade(JudgeGrade head, float percent, float realityHT)
        {
            if (realityHT <= 0f) return head;

            // 与官机不同，这里不会向上修正头判
            if (percent >= 1f)
            {
                switch (head)   // 按满的情况下 good->great, miss->good
                {
                    case JudgeGrade.TooFast:
                        return JudgeGrade.FastGood;
                    case JudgeGrade.Miss:
                        return JudgeGrade.LateGood;
                    case JudgeGrade.FastGood:
                        return JudgeGrade.FastGreat3rd;
                    case JudgeGrade.LateGood:
                        return JudgeGrade.LateGreat3rd;
                    default:
                        return head;
                }
            }
            if (percent >= 0.67f)
            {
                switch (head)   // 按 >=67% 的情况下 good->great, miss->good, cp->p
                {
                    case JudgeGrade.TooFast:
                        return JudgeGrade.FastGood;
                    case JudgeGrade.Miss:
                        return JudgeGrade.LateGood;
                    case JudgeGrade.FastGood:
                        return JudgeGrade.FastGreat3rd;
                    case JudgeGrade.LateGood:
                        return JudgeGrade.LateGreat3rd;
                    case JudgeGrade.FastCritical:
                        return JudgeGrade.FastPerfect2nd;
                    case JudgeGrade.LateCritical:
                        return JudgeGrade.LatePerfect2nd;
                    default:
                        return head;
                }
            }
            if (percent >= 0.33f)
            {
                switch (head)   // 按 >=33% 的情况下 miss->good, cp&p->great
                {
                    case JudgeGrade.TooFast:
                        return JudgeGrade.FastGood;
                    case JudgeGrade.Miss:
                        return JudgeGrade.LateGood;
                    case JudgeGrade.FastCritical:
                    case JudgeGrade.FastPerfect2nd:
                    case JudgeGrade.FastPerfect3rd:
                        return JudgeGrade.FastGreat1st;
                    case JudgeGrade.LateCritical:
                    case JudgeGrade.LatePerfect2nd:
                    case JudgeGrade.LatePerfect3rd:
                        return JudgeGrade.LateGreat1st;
                    default:
                        return head;
                }
            }
            if (percent >= 0.05f)
            {
                return head <= JudgeGrade.FastCritical ? JudgeGrade.FastGood : JudgeGrade.LateGood;
            }

            if (head is JudgeGrade.TooFast or JudgeGrade.Miss) return head;

            return head <= JudgeGrade.FastCritical ? JudgeGrade.FastGood : JudgeGrade.LateGood;
        }


        // ============== SFX ==============
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [BurstCompile]
        public static void PlayTapSound(bool* SfxRequests,
            JudgeGrade grade, bool isBreak, bool isEx, bool isMine, float diff)
        {
            if (isMine && grade is JudgeGrade.Miss or JudgeGrade.TooFast)
            {
                //TODO
                return;
            }

            if (isMine || grade is JudgeGrade.Miss or JudgeGrade.TooFast)
                return;

            if (isBreak)
            {
                switch (grade)
                {
                    case JudgeGrade.LateGood:
                    case JudgeGrade.FastGood:
                    case JudgeGrade.LateGreat1st:
                    case JudgeGrade.LateGreat2nd:
                    case JudgeGrade.LateGreat3rd:
                    case JudgeGrade.FastGreat3rd:
                    case JudgeGrade.FastGreat2nd:
                    case JudgeGrade.FastGreat1st:
                    case JudgeGrade.LatePerfect3rd:
                    case JudgeGrade.FastPerfect3rd:
                    case JudgeGrade.LatePerfect2nd:
                    case JudgeGrade.FastPerfect2nd:
                        SfxRequests[AudioManager.BREAK_JUDGE] = true;
                        break;
                    case JudgeGrade.LateCritical:
                    case JudgeGrade.FastCritical:
                        SfxRequests[AudioManager.BREAK_JUDGE] = true;
                        SfxRequests[AudioManager.BREAK_SFX] = true;
                        break;
                }
                return;
            }

            if (isEx)
            {
                SfxRequests[AudioManager.TAP_EX] = true;
                return;
            }

            switch (grade)
            {
                case JudgeGrade.LateGood:
                case JudgeGrade.FastGood:
                    SfxRequests[AudioManager.TAP_GOOD] = true;
                    break;
                case JudgeGrade.LateGreat1st:
                case JudgeGrade.LateGreat2nd:
                case JudgeGrade.LateGreat3rd:
                case JudgeGrade.FastGreat3rd:
                case JudgeGrade.FastGreat2nd:
                case JudgeGrade.FastGreat1st:
                    SfxRequests[AudioManager.TAP_GREAT] = true;
                    break;
                case JudgeGrade.LatePerfect3rd:
                case JudgeGrade.FastPerfect3rd:
                case JudgeGrade.LatePerfect2nd:
                case JudgeGrade.FastPerfect2nd:
                case JudgeGrade.LateCritical:
                case JudgeGrade.FastCritical:
                    SfxRequests[AudioManager.TAP_PERFECT] = true;
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void PlayHoldSound(bool* SfxRequests,
            JudgeGrade grade, bool isBreak, bool isEx, bool isMine, float diff)
        {
            PlayTapSound(SfxRequests, grade, isBreak, isEx, isMine, diff);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void PlayTouchSound(bool* SfxRequests,
            JudgeGrade grade, bool isMine, bool isHanabi)
        {
            if (isMine && grade is JudgeGrade.Miss or JudgeGrade.TooFast)
            {
                //MEYBE TODO
                return;
            }

            if (isMine || grade is JudgeGrade.Miss or JudgeGrade.TooFast)
                return;

            SfxRequests[AudioManager.TOUCH] = true;
            if (isHanabi)
                SfxRequests[AudioManager.FIREWORK] = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void PlaySlideSound(bool* SfxRequests,
            bool isBreak)
        {
            if (isBreak)
            {
                SfxRequests[AudioManager.BREAK_SLIDE] = true;
            }
            // 官机上无论如何都会播放普通 slide 的启动音
            SfxRequests[AudioManager.SLIDE] = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void PlaySlideEndSound(bool* SfxRequests,
            JudgeGrade grade, bool isMine, bool isBreak)
        {
            if (isMine && grade is JudgeGrade.Miss or JudgeGrade.TooFast)
            {
                //MAYBE TODO
                return;
            }

            if (isMine || grade is JudgeGrade.Miss or JudgeGrade.TooFast)
                return;

            if (isBreak)
            {
                SfxRequests[AudioManager.BREAK_SLIDE_JUDGE] = true;
                // SfxRequests[AudioManager.BREAK_SFX] = true;  // blame @LeZi9916
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetTouchHoldSound(bool* SfxRequests, bool on)
        {
            SfxRequests[AudioManager.TOUCHHOLD] = on;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void PlayAllPerfectSound(bool* SfxRequests)
        {
            SfxRequests[AudioManager.ALL_PERFECT] = true;
        }

        // ============== Effect ==============

        /// <summary>
        /// 播放tap类型打击特效
        /// </summary>
        /// <param name="key">对应的button/sensor位置，注意sensor需要+8，以区分键与A区</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void PlayTapEffect(
            EffectData* JudgeEffectRequests,
            int key, JudgeGrade judge, bool isBreak, bool isMine)
        {
            JudgeEffectRequests[key].Effect = EffectType.Tap;
            JudgeEffectRequests[key].IsBreak = isBreak;
            JudgeEffectRequests[key].IsMine = isMine;
            JudgeEffectRequests[key].JudgeGrade = judge;
        }

        /// <summary>
        /// 播放touch类型打击特效
        /// </summary>
        /// <param name="key">对应的button/sensor位置，注意sensor需要+8，以区分键与A区</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void PlayTouchEffect(
            EffectData* JudgeEffectRequests,
            int key, JudgeGrade judge, bool isBreak, bool isHanabi, bool isMine)
        {
            JudgeEffectRequests[key].Effect = EffectType.Touch;
            if (isHanabi && (judge is not (JudgeGrade.Miss or JudgeGrade.TooFast)))
                JudgeEffectRequests[key].Effect |= EffectType.Firework;
            JudgeEffectRequests[key].IsBreak = isBreak;
            JudgeEffectRequests[key].IsMine = isMine;
            JudgeEffectRequests[key].JudgeGrade = judge;
        }

        /// <summary>
        /// 播放hold按住特效
        /// </summary>
        /// <param name="key">对应的button/sensor位置，注意sensor需要+8，以区分键与A区</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetHoldEffect(
            EffectData* JudgeEffectRequests,
            int key, JudgeGrade judge, bool hasHolding)
        {
            JudgeEffectRequests[key].HasHolding = hasHolding;
            if (!hasHolding) return;

            switch (judge)
            {
                case JudgeGrade.LateGood:
                case JudgeGrade.FastGood:
                    JudgeEffectRequests[key].HoldingColor = new Color(0.56f, 1f, 0.59f); // Green
                    break;
                case JudgeGrade.LateGreat1st:
                case JudgeGrade.LateGreat2nd:
                case JudgeGrade.LateGreat3rd:
                case JudgeGrade.FastGreat3rd:
                case JudgeGrade.FastGreat2nd:
                case JudgeGrade.FastGreat1st:
                    JudgeEffectRequests[key].HoldingColor = new Color(1f, 0.70f, 0.94f); // Pink
                    break;
                case JudgeGrade.LatePerfect3rd:
                case JudgeGrade.FastPerfect3rd:
                case JudgeGrade.LatePerfect2nd:
                case JudgeGrade.FastPerfect2nd:
                case JudgeGrade.LateCritical:
                case JudgeGrade.FastCritical:
                    JudgeEffectRequests[key].HoldingColor = new Color(1f, 0.93f, 0.61f); // Gold
                    break;
                case JudgeGrade.Miss:
                case JudgeGrade.TooFast:
                    JudgeEffectRequests[key].HoldingColor = new Color(1f, 1f, 1f); // White
                    break;
            }
        }

        // ============== Report ==============

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReportResult(
            NativeList<ReportResultEntry>.ParallelWriter ReportResults,
            JudgeGrade grade, bool isBreak, SimaiNoteType noteType)
        {
            ReportResults.AddNoResize(new ReportResultEntry
            {
                Grade = grade,
                IsBreak = isBreak,
                NoteType = noteType,
            });
        }
    }
}