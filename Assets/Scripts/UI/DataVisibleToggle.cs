using UnityEngine;
using UnityEngine.UI;

namespace MajdataViewX.UI
{
    public class DataVisibleToggle : MonoBehaviour
    {
        public Sprite onSprite;
        public Sprite offSprite;
        public GameObject[] targets;

        private Toggle toggle;
        private Image icon;
        private static bool? savedState;

        private void Awake()
        {
            toggle = GetComponent<Toggle>();
            icon = GetComponent<Image>();
        }

        private void Start()
        {
            if (savedState.HasValue)
            {
                toggle.SetIsOnWithoutNotify(savedState.Value);
                savedState = null;
            }

            Apply(toggle.isOn);
            toggle.onValueChanged.AddListener(Apply);
        }

        private void Apply(bool on)
        {
            icon.sprite = on ? onSprite : offSprite;
            foreach (var target in targets)
            {
                if (target != null)
                    target.SetActive(on);
            }
        }

        private void OnDestroy()
        {
            if (toggle != null)
                savedState = toggle.isOn;
        }
    }
}
