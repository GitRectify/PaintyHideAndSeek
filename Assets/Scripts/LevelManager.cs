using System;
using UnityEngine;

// NOTE: Resolves a previously "never-reversed-but-referenced" class (only call sites were known
// from BotManager/CheckRateGame/etc. so far: SetLevel, HideRoom, CurrentRoomName, ApplyActiveRoom,
// TryGetSpawn). Confirms the third singleton pattern already noted (bare static Instance field,
// same style as AdMgr/SelectPlayerUI) and a 5th occurrence of the scene-lookup helper — this one
// is static, unlike the instance-method versions seen in JoystickMover/GameModeManager/etc.
public class LevelManager : MonoBehaviour
{
    // NOTE: nested LevelDef type — only these 4 fields are confirmed by usage in this paste.
    // It may have additional fields not exercised by any method decompiled so far.
    [Serializable]
    public class LevelDef
    {
        public string roomName;
        public string displayName;
        public Vector3 playerSpawnPos;
        public Vector3 playerSpawnEuler;
    }

    [SerializeField] private LevelDef[] levels = new LevelDef[]
    {
        new LevelDef
        {
            roomName = "Room",
            displayName = "Room",
            playerSpawnPos = new Vector3(0,7.5f,0),
            playerSpawnEuler = Vector3.zero
        },

        new LevelDef
        {
            roomName = "Room2",
            displayName = "Room 2",
            playerSpawnPos = new Vector3(0,7.5f,0),
            playerSpawnEuler = Vector3.zero
        },

        new LevelDef
        {
            roomName = "Room3",
            displayName = "Room 3",
            playerSpawnPos = new Vector3(0,7.5f,0),
            playerSpawnEuler = Vector3.zero
        },

        new LevelDef
        {
            roomName = "Room4",
            displayName = "Room 4",
            playerSpawnPos = new Vector3(0,7.5f,0),
            playerSpawnEuler = Vector3.zero
        },

        new LevelDef
        {
            roomName = "Room5",
            displayName = "Room 5",
            playerSpawnPos = new Vector3(0,7.5f,0),
            playerSpawnEuler = Vector3.zero
        }
    };
    [SerializeField] private string prefsKey = "SelectedLevel";
    [SerializeField] private string prefabDir = "Rooms/";

    private int current;
    private GameObject _roomInstance;
    public GameObject CurrentRoomInstance => _roomInstance;
    private string _loadedRoomName;
    private string lastAppliedRoom;

    // NOTE: invoked as a plain delegate call in the decompiled code (no distinguishable
    // add/remove accessor complexity at the IL2CPP level) — could equally be a C# event;
    // kept as a plain public field since that's the simpler, equally-valid reading.
    public Action onLevelChanged;

    public static LevelManager Instance { get; private set; }

    public int Count => levels != null ? levels.Length : 0;

    public int Current
    {
        get
        {
            if (levels != null && levels.Length > 0)
            {
                return Mathf.Clamp(current, 0, levels.Length - 1);
            }
            return 0;
        }
    }

    public string CurrentRoomName
    {
        get
        {
            if (levels != null && levels.Length > 0)
            {
                int idx = Mathf.Clamp(current, 0, levels.Length - 1);
                return levels[idx].roomName;
            }
            return "Room";
        }
    }

    public string CurrentDisplayName
    {
        get
        {
            if (levels != null && levels.Length > 0)
            {
                int idx = Mathf.Clamp(current, 0, levels.Length - 1);
                return levels[idx].displayName;
            }
            return "Room";
        }
    }

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }

        current = PlayerPrefs.GetInt(prefsKey, 0);
        if (levels != null && levels.Length > 0)
        {
            current = Mathf.Clamp(current, 0, levels.Length - 1);
        }
    }

    public void SetLevel(int i)
    {
        if (levels == null || levels.Length == 0)
        {
            return;
        }

        current = Mathf.Clamp(i, 0, levels.Length - 1);
        PlayerPrefs.SetInt(prefsKey, current);
        onLevelChanged?.Invoke();
    }

    public void Cycle()
    {
        if (levels == null || levels.Length <= 1)
        {
            return;
        }

        int count = levels.Length;
        int clamped = Mathf.Clamp(current, 0, count - 1);
        int next = (clamped + 1) % count;

        current = next;
        PlayerPrefs.SetInt(prefsKey, next);
        onLevelChanged?.Invoke();
    }

    public void HideRoom()
    {
        if (_roomInstance == null)
        {
            return;
        }
        if (!_roomInstance.activeSelf)
        {
            return;
        }
        _roomInstance.SetActive(false);
    }

    // NOTE: returns false both when the requested room is already loaded (just re-activating it
    // if it was hidden) and when the prefab couldn't be found — true only on an actual fresh
    // instantiate. Matches the decompiled control flow exactly.
    public bool ApplyActiveRoom()
    {
        string roomName = CurrentRoomName;

        if (_roomInstance != null && _loadedRoomName == roomName)
        {
            if (!_roomInstance.activeSelf)
            {
                _roomInstance.SetActive(true);
            }
            return false;
        }

        string path = prefabDir + roomName;
        Debug.Log($"[LevelManager] Trying to load room: " +$"Assets/Resources/{path}.prefab");
        GameObject prefab = Resources.Load<GameObject>(path);

        if (prefab == null)
        {
            Debug.LogWarning($"[LevelManager] Room prefab not found: Resources/{prefabDir}{roomName} \u2014 create it at Assets/Resources/{prefabDir}{roomName}.prefab.");
            return false;
        }

        Debug.Log($"[LevelManager] Successfully loaded prefab: {prefab.name}");

        GameObject previous = _roomInstance;
        _roomInstance = Instantiate(
            prefab,
            Vector3.zero,
            Quaternion.identity
        );
        _roomInstance.name = roomName;
        _roomInstance.SetActive(true);
        _loadedRoomName = roomName;

        Debug.Log($"[LevelManager] Instantiated room: {_roomInstance.name}");

        if (previous != null)
        {
            DestroyImmediate(previous);
        }
        Resources.UnloadUnusedAssets();
        lastAppliedRoom = roomName;
        return true;
    }

    public bool TryGetSpawn(out Vector3 pos, out Quaternion rot)
    {
        pos = Vector3.zero;
        rot = Quaternion.identity;

        if (levels == null || levels.Length == 0)
        {
            return false;
        }

        int idx = Mathf.Clamp(current, 0, levels.Length - 1);
        LevelDef def = levels[idx];

        pos = def.playerSpawnPos;
        // NOTE: decompiled code manually converts degrees->radians (x 0.017453292 = Mathf.Deg2Rad)
        // and calls the internal Quaternion.Internal_FromEulerRad — behaviourally identical to
        // the public Quaternion.Euler(degrees) API used here.
        rot = Quaternion.Euler(def.playerSpawnEuler);
        return true;
    }

    // NOTE: not called by anything in this paste (no caller for it decompiled so far) —
    // included since it was part of the same class, presumably wired to a room GameObject
    // lookup elsewhere in this class not yet decompiled.
    private static GameObject FindInScene(string n)
    {
        GameObject go = GameObject.Find(n);
        if (go != null)
        {
            return go;
        }

        Transform[] all = Resources.FindObjectsOfTypeAll<Transform>();
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t.name == n && t.gameObject.scene.IsValid())
            {
                return t.gameObject;
            }
        }
        return null;
    }
}