#nullable enable


using Cysharp.Threading.Tasks;
using MajdataViewX.Base;
using MajdataViewX.Utils;
using System;
using System.Collections;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

using static MajdataViewX.Base.MajCtx;

namespace MajdataViewX.Managers
{
    public class BgManager : MonoBehaviour
    {
        [SerializeField]
        private Sprite bgDummy;
        [SerializeField]
        private Sprite defaultBg;

        [SerializeField]
        private Material fullscreenBgMaterial;
        [SerializeField]
        private Material circledBgMaterial;

        public bool ResizeBg;

        private RawImage jacketImage;
        private GameObject songDetail;
        private static readonly int ShowHash = Animator.StringToHash("show");
        private Animator detailAnim;
        private SpriteRenderer spriteRender;
        private VideoPlayer videoPlayer;

        private float smoothRDelta;

        public float PvOffset { get; set; }

        private const float CIRCLED_SCALE_X = 1.1f;
        private const float FULLSCREEN_SCALE_X = 1.777f;

        private Sprite? Bg { get; set; }
        private string? VideoUrl { get; set; }

        public static bool hasBg;
        public static bool hasVideo;
        public bool IsBgLoaded => !hasBg || Bg != null;
        public bool IsVideoLoaded => !hasVideo || !string.IsNullOrWhiteSpace(VideoUrl);

        private static Sprite? _emptySprite;
        bool _videoPaused;
        bool _videoWaitingForOffset;
        bool _videoStopped;
        private Coroutine? _videoWaitCoroutine;

        private ExternalPvDecoder? _externalDecoder;
        private ExternalPvDisplay? _pvDisplay;
        private string? _videoFilePath;
        private int _externalPvFps;
        private bool _externalPrepInProgress;

        /// <summary>录制期是否由外部 FFmpeg 解码 PV（失败时保持 false，走 VideoPlayer）。</summary>
        public bool IsExternalPvActive { get; private set; }

        private void Awake()
        {
            _bgManager = this;
        }

        private void Start()
        {
            jacketImage = GameObject.Find("Jacket").GetComponent<RawImage>();
            songDetail = GameObject.Find("CanvasSongDetail");
            songDetail.SetActive(false);

            spriteRender = GetComponent<SpriteRenderer>();
            videoPlayer = GetComponent<VideoPlayer>();
            detailAnim = songDetail.GetComponent<Animator>();

            _emptySprite = Sprite.Create(new Texture2D(1080, 1080), new Rect(0, 0, 1080, 1080), new Vector2(0.5f, 0.5f));
        }

        private void Update()
        {
            if (!hasVideo || _videoStopped) return;
            if (_externalPrepInProgress || IsExternalPvActive) return;

            if (_videoPaused)
            {
                videoPlayer.time = GetPvTime();
                videoPlayer.Play();
                videoPlayer.Pause();
                return;
            }
            var rawPvTime = _timeProvider.AudioTime - PvOffset;
            if (rawPvTime <= 0)
            {
                videoPlayer.time = 0;
                videoPlayer.Pause();
                _videoWaitingForOffset = true;
                return;
            }

            if (_videoWaitingForOffset)
            {
                videoPlayer.time = rawPvTime;
                videoPlayer.Play();
                _videoWaitingForOffset = false;
                return;
            }

            var pvTime = GetPvTime();
            var delta = (float)videoPlayer.clockTime - pvTime;
            smoothRDelta += (Time.unscaledDeltaTime - smoothRDelta) * 0.01f;
            if (_timeProvider.AudioTime < 0) return;
            var realSpeed = Time.deltaTime / smoothRDelta;

            if (Time.captureFramerate != 0)
            {
                videoPlayer.playbackSpeed = realSpeed - delta;
                return;
            }

            if (delta < -0.01f)
                videoPlayer.playbackSpeed = _timeProvider.CurrentSpeed + 0.2f;
            else if (delta > 0.01f)
                videoPlayer.playbackSpeed = _timeProvider.CurrentSpeed - 0.2f;
            else
                videoPlayer.playbackSpeed = _timeProvider.CurrentSpeed;
        }

        private float GetPvTime() => Mathf.Max(0f, _timeProvider.AudioTime - PvOffset);

        /// <summary>录制前尝试用外部 FFmpeg 顺序解码 PV；任何失败返回 false，由调用方回退 VideoPlayer。</summary>
        public async UniTask<bool> TryPrepareExternalPvAsync(int fps, int exportWidth, int exportHeight)
        {
            if (IsExternalPvActive) return true;
            if (!hasVideo || string.IsNullOrWhiteSpace(_videoFilePath) || fps <= 0)
                return false;

            var ffmpegPath = MajEnv.ExternalFfmpegPath;
            if (ffmpegPath == null)
                return false;

            _externalPrepInProgress = true;
            try
            {
                // 仅用 VideoPlayer 校验视频可打开，随后停止，避免其继续解码渲染。
                if (_videoWaitCoroutine != null)
                {
                    StopCoroutine(_videoWaitCoroutine);
                    _videoWaitCoroutine = null;
                }

                videoPlayer.Stop();
                videoPlayer.url = VideoUrl;
                videoPlayer.Prepare();

                var deadline = Time.realtimeSinceStartup + 5f;
                while (!videoPlayer.isPrepared)
                {
                    if (Time.realtimeSinceStartup > deadline)
                    {
                        videoPlayer.Stop();
                        RestoreVideoPlayerFallback();
                        return false;
                    }
                    await UniTask.Yield();
                }

                if (exportWidth <= 0 || exportHeight <= 0)
                {
                    videoPlayer.Stop();
                    RestoreVideoPlayerFallback();
                    return false;
                }

                videoPlayer.Stop();

                _externalPvFps = fps;
                _externalDecoder = new ExternalPvDecoder(ffmpegPath, _videoFilePath, fps, exportWidth, exportHeight);
                if (!_externalDecoder.TryStart(out var error))
                {
                    Debug.LogWarning($"[Export] 外部 FFmpeg 启动失败，回退 VideoPlayer：{error}");
                    AbortExternalPv();
                    RestoreVideoPlayerFallback();
                    return false;
                }

                // 首帧就绪校验：解不出第 0 帧则整段回退。
                var first = await _externalDecoder.ReadFrameAtOrBeforeAsync(0, startup: true, CancellationToken.None);
                if (first == null)
                {
                    AbortExternalPv();
                    RestoreVideoPlayerFallback();
                    return false;
                }

                // 用外部纹理创建 Sprite 并直接绑定到 SpriteRenderer：sprite 的 _MainTex 即该纹理，
                // 后续 LoadRawTextureData/Apply 更新同一纹理即可逐帧刷新，避免属性块失效导致白屏。
                _pvDisplay ??= new ExternalPvDisplay(spriteRender, fullscreenBgMaterial, circledBgMaterial);
                if (!_pvDisplay.Create(exportWidth, exportHeight, ResizeBg))
                {
                    AbortExternalPv();
                    RestoreVideoPlayerFallback();
                    return false;
                }

                _pvDisplay.ApplyFrame(first);
                _externalDecoder.ReleaseFrame(first);
                IsExternalPvActive = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Export] 外部 FFmpeg 准备失败，回退 VideoPlayer：{ex}");
                AbortExternalPv();
                RestoreVideoPlayerFallback();
                return false;
            }
            finally
            {
                _externalPrepInProgress = false;
            }
        }

        /// <summary>每输出帧捕获前调用：按 PV 目标时间取外部解码帧并上传，解码不足时等待（背压）。</summary>
        public async UniTask PresentExternalPvFrameAsync()
        {
            if (!IsExternalPvActive || _externalDecoder == null) return;

            var pvTime = Mathf.Max(0f, _timeProvider.AudioTime - PvOffset);
            var targetIndex = (long)Math.Floor(pvTime * _externalPvFps);
            if (_pvDisplay == null || targetIndex <= _pvDisplay.LastAppliedIndex) return;

            var frame = await _externalDecoder.ReadFrameAtOrBeforeAsync(targetIndex, startup: false, CancellationToken.None);
            if (frame == null) return; // 已到 EOF，保持最后一帧

            _pvDisplay.ApplyFrame(frame);
            _externalDecoder.ReleaseFrame(frame);
        }

        public void AbortExternalPv()
        {
            IsExternalPvActive = false;
            if (_externalDecoder != null)
            {
                _externalDecoder.Dispose();
                _externalDecoder = null;
            }
            _pvDisplay?.Release();
        }

        private void RestoreVideoPlayerFallback()
        {
            if (hasVideo && !_videoStopped)
                ShowVideo();
        }

        public void PlaySongDetail()
        {
            songDetail.SetActive(true);
            detailAnim.SetTrigger(ShowHash);
        }

        public void LoadBG(string path)
        {
            DestroyLoadedBackground();
            Bg = TexLoader.LoadSprite(path);
        }

        private void DestroyLoadedBackground()
        {
            if (Bg != null)
            {
                if (Bg.texture != null)
                    Destroy(Bg.texture);

                Destroy(Bg);
                Bg = null;
            }
        }

        public void ShowBG()
        {
            if (Bg == null || !hasBg)
            {
                jacketImage.texture = bgDummy.texture;
                spriteRender.sprite = defaultBg;
                return;
            }

            jacketImage.texture = Bg.texture;
            spriteRender.sprite = Bg;
            var scale = 1140f / Bg.texture.width;
            gameObject.transform.localScale = new Vector3(scale, scale, scale);
        }

        public void LoadVideo(string path)
        {
            StopVideo();
            _videoFilePath = path;
            VideoUrl = "file://" + path;
        }

        public void ShowVideo()
        {
            if (!hasVideo) return;
            _videoStopped = false;

            if (_videoWaitCoroutine != null)
            {
                StopCoroutine(_videoWaitCoroutine);
                _videoWaitCoroutine = null;
            }

            // 开始/恢复播放时清除暂停标志，避免协程误判为暂停而跳过尺寸计算
            _videoPaused = false;

            videoPlayer.url = VideoUrl;
            _videoWaitCoroutine = StartCoroutine(WaitFumenStart());
            IEnumerator WaitFumenStart()
            {
                videoPlayer.Prepare();

                //secret hack: if not so, the bg won't be set to defaultBg but full white
                spriteRender.sprite = _emptySprite;

                while (_timeProvider.AudioTime <= 0) yield return new WaitForEndOfFrame();
                while (!videoPlayer.isPrepared) yield return new WaitForEndOfFrame();

                // 若在准备期间被暂停，则保持暂停，避免旧协程覆盖暂停状态
                if (_videoPaused)
                {
                    videoPlayer.Pause();
                    _videoWaitCoroutine = null;
                    yield break;
                }

                videoPlayer.time = GetPvTime();
                _videoPaused = false;
                _videoWaitingForOffset = _timeProvider.AudioTime - PvOffset <= 0;
                if (_videoWaitingForOffset)
                    videoPlayer.Pause();
                else
                    videoPlayer.Play();

                var scale = videoPlayer.height / (float)videoPlayer.width;
                if (ResizeBg)
                {
                    gameObject.transform.localScale = new Vector3(FULLSCREEN_SCALE_X, FULLSCREEN_SCALE_X * scale);
                    spriteRender.material = fullscreenBgMaterial;
                }
                else
                {
                    var circleDiameter = circledBgMaterial.GetFloat("_Radius") * 2f;
                    var spriteSize = spriteRender.sprite.bounds.size;
                    var longestSide = Mathf.Max(spriteSize.x, spriteSize.y * scale);
                    var fitScale = circleDiameter / longestSide;
                    gameObject.transform.localScale = new Vector3(fitScale, fitScale * scale, fitScale);
                    spriteRender.material = circledBgMaterial;
                }

                _videoWaitCoroutine = null;
            }
        }

        public void PauseVideo()
        {
            if (!hasVideo) return;
            videoPlayer.Pause();
            _videoPaused = true;
        }

        public void StopVideo()
        {
            if (_videoWaitCoroutine != null)
            {
                StopCoroutine(_videoWaitCoroutine);
                _videoWaitCoroutine = null;
            }
            videoPlayer.Stop();
            _videoPaused = false;
            _videoWaitingForOffset = false;
            AbortExternalPv();
        }

        public void ClearVideo()
        {
            StopVideo();
            VideoUrl = null;
            _videoFilePath = null;
        }

        public void ResetState()
        {
            if (_videoWaitCoroutine != null)
            {
                StopCoroutine(_videoWaitCoroutine);
                _videoWaitCoroutine = null;
            }
            AbortExternalPv();
            _externalPrepInProgress = false;
            videoPlayer.Stop();
            videoPlayer.url = ""; // 释放视频文件句柄，否则文件被占用无法删除
            VideoUrl = null;
            _videoFilePath = null;
            hasBg = false;
            hasVideo = false;
            _videoStopped = true;
            _videoPaused = false;
            _videoWaitingForOffset = false;
            // 销毁上一曲背景图(Texture2D/Sprite)，避免滞留到下次 LoadBG
            DestroyLoadedBackground();
            gameObject.transform.localScale = new Vector3(CIRCLED_SCALE_X, CIRCLED_SCALE_X, CIRCLED_SCALE_X);
            spriteRender.material = circledBgMaterial;
            spriteRender.sprite = defaultBg;
            smoothRDelta = 0f;

            if (songDetail != null)
                songDetail.SetActive(false);
        }

        private void OnDestroy()
        {
            AbortExternalPv();
            DestroyLoadedBackground();
            if (_emptySprite != null)
            {
                var texture = _emptySprite.texture;
                Destroy(_emptySprite);
                if (texture != null)
                    Destroy(texture);
                _emptySprite = null;
            }
        }
    }
}