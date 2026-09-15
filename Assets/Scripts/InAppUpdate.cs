using UnityEngine;

namespace Dev.Scripts.Common
{
    public class InAppUpdate : MonoBehaviour
    {
        public static InAppUpdate Instance { get; private set; }

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else if (Instance != this)
            {
                Destroy(gameObject);
            }
        }

        private void Start()
        {
            // Google Play in-app update intentionally disabled.
        }
    }
}