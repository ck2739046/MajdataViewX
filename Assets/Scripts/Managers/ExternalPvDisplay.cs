#nullable enable

using UnityEngine;

namespace MajdataViewX.Managers
{
    /// <summary>外部解码 PV 帧的 Unity 显示承载：负责纹理、Sprite、材质/缩放与帧上传。</summary>
    internal sealed class ExternalPvDisplay
    {
        private const float FULLSCREEN_SCALE_X = 1.777f;

        private readonly SpriteRenderer _spriteRender;
        private readonly Material _fullscreenMaterial;
        private readonly Material _circledMaterial;
        private Texture2D? _texture;
        private Sprite? _sprite;
        private long _lastAppliedIndex = long.MinValue;

        internal ExternalPvDisplay(
            SpriteRenderer spriteRender,
            Material fullscreenMaterial,
            Material circledMaterial)
        {
            _spriteRender = spriteRender;
            _fullscreenMaterial = fullscreenMaterial;
            _circledMaterial = circledMaterial;
        }

        internal long LastAppliedIndex => _lastAppliedIndex;

        internal bool Create(int width, int height, bool resizeBg)
        {
            _texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "External PV Frame",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            _sprite = Sprite.Create(_texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f));
            ApplyScale(resizeBg);
            return _sprite != null;
        }

        internal bool ApplyFrame(ExternalPvDecoder.Frame frame)
        {
            if (_texture == null || frame.Index == _lastAppliedIndex)
                return false;

            _texture.LoadRawTextureData(frame.Data);
            _texture.Apply();
            _lastAppliedIndex = frame.Index;
            return true;
        }

        internal void Release()
        {
            _lastAppliedIndex = long.MinValue;
            if (_sprite != null)
            {
                Object.Destroy(_sprite);
                _sprite = null;
            }
            if (_texture != null)
            {
                Object.Destroy(_texture);
                _texture = null;
            }
            if (_spriteRender != null)
                _spriteRender.SetPropertyBlock(null);
        }

        private void ApplyScale(bool resizeBg)
        {
            if (_sprite == null) return;

            _spriteRender.sprite = _sprite;
            var size = _sprite.bounds.size;
            var aspect = size.y / size.x;
            if (resizeBg)
            {
                _spriteRender.transform.localScale = new Vector3(FULLSCREEN_SCALE_X, FULLSCREEN_SCALE_X * aspect);
                _spriteRender.material = _fullscreenMaterial;
            }
            else
            {
                var circleDiameter = _circledMaterial.GetFloat("_Radius") * 2f;
                var longestSide = Mathf.Max(size.x, size.y);
                var fitScale = circleDiameter / longestSide;
                _spriteRender.transform.localScale = new Vector3(fitScale, fitScale * aspect, fitScale);
                _spriteRender.material = _circledMaterial;
            }
        }
    }
}
