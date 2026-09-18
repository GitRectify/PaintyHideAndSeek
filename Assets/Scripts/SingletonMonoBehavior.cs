using UnityEngine;

public class SingletonMonoBehavior<T> : MonoBehaviour
    where T : MonoBehaviour
{
    private static T _instance;

    public static T Instance
    {
        get
        {
            if (_instance == null)
            {
                #if UNITY_6000_0_OR_NEWER
                    _instance = Object.FindFirstObjectByType<T>();
                #else
                    _instance = Object.FindObjectOfType<T>();
                #endif
            }

            return _instance;
        }

        protected set
        {
            _instance = value;
        }
    }

    protected virtual void Awake()
    {
        T current = this as T;

        if (_instance == null)
        {
            _instance = current;
        }
        else if (_instance != current)
        {
            Destroy(gameObject);
        }
    }
}