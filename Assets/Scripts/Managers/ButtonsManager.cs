using UnityEngine;
using UnityEngine.UI;


namespace MajdataViewX.Managers
{
    public class ButtonsManager : MonoBehaviour
    {
        [SerializeField]
        private Dropdown DDResolution;

        private void Start()
        {
            DDResolution.gameObject.SetActive(false);
        }

        public void ToggleFullscreen()
        {
            Debug.Log("ToggleFullScreen");
            var resolutions = Screen.resolutions;
            if (Screen.fullScreen)
            {
                var width = 512;
                var height = 512;
                Screen.SetResolution(width, height, false);
            }
            else
            {
                Screen.SetResolution(resolutions[resolutions.Length - 1].width, resolutions[resolutions.Length - 1].height, true);
            }

            Screen.fullScreenMode = FullScreenMode.FullScreenWindow;
        }

        public void DisplayDropdown()
        {
            DDResolution.value = 999;
            DDResolution.gameObject.SetActive(true);
        }

        public void SetResolution()
        {
            var i = DDResolution.value;
            Debug.Log(i);
            switch (i)
            {
                case 0:
                    Screen.SetResolution(512, 512, false);
                    break;
                case 1:
                    Screen.SetResolution(1080, 1080, false);
                    break;
                case 2:
                    Screen.SetResolution(1280, 720, false);
                    break;
                case 3:
                    Screen.SetResolution(1920, 1080, false);
                    break;
                case 4:
                    Screen.SetResolution(2560, 1440, false);
                    break;
                case 5:
                    Screen.SetResolution(3840, 2160, false);
                    break;
            }

            DDResolution.gameObject.SetActive(false);
        }
    }
}
