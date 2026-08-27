#nullable enable


using MajdataViewX.Utils;
using System.Collections;
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
            if (hasVideo && _videoPaused)
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
            VideoUrl = "file://" + path;
        }

        public void ShowVideo()
        {
            if (!hasVideo) return;

            videoPlayer.url = VideoUrl;
            StartCoroutine(WaitFumenStart());
            IEnumerator WaitFumenStart()
            {
                videoPlayer.Prepare();

                //secret hack: if not so, the bg won't be set to defaultBg but full white
                spriteRender.sprite = _emptySprite;

                while (_timeProvider.AudioTime <= 0) yield return new WaitForEndOfFrame();
                while (!videoPlayer.isPrepared) yield return new WaitForEndOfFrame();
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
            }
        }

        public void PauseVideo()
        {
            if (!hasVideo) return;
            videoPlayer.Pause();
            _videoPaused = true;
        }


        public void ResetState()
        {
            videoPlayer.Stop();
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