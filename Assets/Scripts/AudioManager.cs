using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class AudioManager : MonoBehaviour
{
    public static AudioManager Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = Object.FindFirstObjectByType<AudioManager>();
                if (_instance == null)
                {
                    GameObject go = GameObject.Find("AudioManager");
                    if (go == null)
                    {
                        go = new GameObject("AudioManager");
                    }
                    _instance = go.AddComponent<AudioManager>();
                }
            }
            return _instance;
        }
    }
    private static AudioManager _instance;

    // CORRECTION: confirmed exact-duplicate of get_Instance's body (per this project's
    // established convention for exact-duplicate methods) — implemented once, forwarded here.
    // The earlier draft omitted this method entirely.
    public static void Bootstrap()
    {
        _ = Instance;
    }

    public float paintCooldown = 0.1f;
    public float poseVolume = 0.7f;
    public float gunVolume = 0.9f;
    public float buttonVolume = 0.2f;
    public float paintVolume = 0.55f;
    public float musicVolume = 0.45f;
    public int countdownFrom = 5;
    public float clockVolume = 0.6f;
    public float bellVolume = 0.7f;
    public float resultVolume = 0.8f;
    public bool autoHookButtons = true;
    public string[] tapExcludeNames = { "FireBtn", "Slot1", "Slot2", "Slot3", "Slot4" };

    public AudioClip buttonTap;
    public AudioClip paintStroke;
    public AudioClip poseChange;
    public AudioClip waterGunShot;
    public AudioClip themeMenu;
    public AudioClip themeGame;
    public AudioClip clockTick;
    public AudioClip phaseBellStart;
    public AudioClip phaseBellSeek;
    public AudioClip winSfx;
    public AudioClip loseSfx;

    private float _lastPaint = -99f;
    private int _lastTickSec = -1;
    private readonly HashSet<Button> _hooked = new HashSet<Button>();
    private bool _musicOn = true;
    private bool _sfxOn = true;
    private AudioSource _music;
    private AudioSource _src;

    // No custom ctor needed — all of the above are now field initializers matching the
    // decompiled ctor's assignments.

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }

        _instance = this;
        ResolveClips();
        EnsureSource();
        EnsureListener();

        _musicOn = PlayerPrefs.GetInt("set_music", 1) == 1;
        _sfxOn = PlayerPrefs.GetInt("set_sound", 1) == 1;
    }

    private void OnEnable()
    {
        if (autoHookButtons)
        {
            InvokeRepeating("HookButtons", 0.1f, 0.5f);
        }
    }

    private void OnDisable()
    {
        CancelInvoke("HookButtons");
    }

    // ====================== SETUP ======================

    private void ResolveClips()
    {
        if (buttonTap == null) buttonTap = Resources.Load<AudioClip>("Sfx/01_button_tap_soft");
        if (paintStroke == null) paintStroke = Resources.Load<AudioClip>("Sfx/02_paint_brush");
        if (poseChange == null) poseChange = Resources.Load<AudioClip>("Sfx/03_pose_change_soft");
        if (waterGunShot == null) waterGunShot = Resources.Load<AudioClip>("Sfx/04_water_gun_shot");
        if (themeMenu == null) themeMenu = Resources.Load<AudioClip>("Sfx/Theme");
        if (themeGame == null) themeGame = Resources.Load<AudioClip>("Sfx/Theme2");
        if (clockTick == null) clockTick = Resources.Load<AudioClip>("Sfx/clock_tick_clear_countdown");
        if (phaseBellStart == null) phaseBellStart = Resources.Load<AudioClip>("Sfx/phase_bell_start_two_tone");
        if (phaseBellSeek == null) phaseBellSeek = Resources.Load<AudioClip>("Sfx/phase_bell_2_rings_soft");
        if (winSfx == null) winSfx = Resources.Load<AudioClip>("Sfx/Win");
        if (loseSfx == null) loseSfx = Resources.Load<AudioClip>("Sfx/Lose");
    }

    private void EnsureSource()
    {
        _src = GetComponent<AudioSource>();
        if (_src == null)
        {
            _src = gameObject.AddComponent<AudioSource>();
        }
        _src.playOnAwake = false;
        _src.spatialBlend = 0f;
        _src.ignoreListenerPause = true;
    }

    private void EnsureMusicSource()
    {
        if (_music != null) return;

        _music = gameObject.AddComponent<AudioSource>();
        _music.playOnAwake = false;
        _music.loop = true;
        _music.spatialBlend = 0f;
    }

    private void EnsureListener()
    {
        if (Object.FindFirstObjectByType<AudioListener>() == null)
        {
            // NOTE: confirmed via trace — falls back to THIS component's own gameObject if
            // Camera.main is null, not just skipping listener creation entirely.
            GameObject target = (Camera.main != null) ? Camera.main.gameObject : gameObject;
            target.AddComponent<AudioListener>();
        }
    }

    private void HookButtons()
    {
        // NOTE: (1, 0) read as (FindObjectsInactive.Include, FindObjectsSortMode.None) — matches
        // Unity's documented enum ordinals for this overload, though the decompile itself only
        // shows the raw ints, not the enum names.
        Button[] buttons = Object.FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        foreach (Button btn in buttons)
        {
            if (btn != null && !_hooked.Contains(btn))
            {
                _hooked.Add(btn);
                if (!IsExcluded(btn.name))
                {
                    btn.onClick.AddListener(ButtonTap);
                }
            }
        }
    }

    private bool IsExcluded(string n)
    {
        if (tapExcludeNames == null) return false;

        foreach (string exclude in tapExcludeNames)
        {
            if (!string.IsNullOrEmpty(exclude))
            {
                // CORRECTION: confirmed via trace to throw naturally if `n` is null here (the
                // decompile breaks out to a throw path specifically when n is null, on the first
                // non-empty exclude entry reached) — the earlier draft's `n != null` guard
                // silently treated that as "not excluded" instead, which doesn't match.
                if (n.Contains(exclude)) return true;
            }
        }

        return false;
    }

    // ====================== PLAYBACK ======================

    private void PlayClip(AudioClip clip, float vol)
    {
        if (_sfxOn && clip != null)
        {
            if (_src == null)
            {
                EnsureSource();
            }
            float volumeScale = Mathf.Clamp01(vol);
            _src.PlayOneShot(clip, volumeScale);
        }
    }

    private void PlayMusic(AudioClip clip)
    {
        if (clip == null) return;

        EnsureMusicSource();
        _music.mute = !_musicOn;

        if (_music.clip == clip && _music.isPlaying)
        {
            return;
        }

        _music.clip = clip;
        _music.volume = Mathf.Clamp01(musicVolume);
        _music.Play();
    }

    private void ApplyMusicEnabled(bool on)
    {
        _musicOn = on;
        if (_music != null)
        {
            _music.mute = !on;
        }
    }

    // ====================== PUBLIC STATIC API ======================
    // CORRECTION throughout this section: the earlier draft called the `Instance` property
    // multiple times per method (e.g. `Instance.PlayClip(Instance.buttonTap, Instance.buttonVolume)`
    // invokes the getter three times). The real decompile calls get_Instance() exactly ONCE per
    // method and reuses the result. Since Instance's getter has real side effects (it can create
    // a whole new GameObject + component if none exists), repeated invocation isn't just a style
    // difference — fixed to cache the instance in a local once throughout.

    public static bool IsMusicOn
    {
        get
        {
            AudioManager inst = Instance;
            return inst == null || inst._musicOn;
        }
    }

    public static bool IsSfxOn
    {
        get
        {
            AudioManager inst = Instance;
            return inst == null || inst._sfxOn;
        }
    }

    public static void SetMusicEnabled(bool on)
    {
        Instance.ApplyMusicEnabled(on);
    }

    public static void SetSfxEnabled(bool on)
    {
        // NOTE: confirmed asymmetric with SetMusicEnabled — this is a bare field write with no
        // side effect on any currently-playing audio, unlike SetMusicEnabled which also updates
        // the music AudioSource's mute state via ApplyMusicEnabled. Not an omission on my part.
        Instance._sfxOn = on;
    }

    public static void MusicMenu()
    {
        AudioManager inst = Instance;
        inst.PlayMusic(inst.themeMenu);
    }

    public static void MusicGame()
    {
        AudioManager inst = Instance;
        inst.PlayMusic(inst.themeGame);
    }

    // CORRECTION: there is only ONE StopMusic in the real decompile (this static method) — it
    // manipulates _music directly rather than delegating to a separate instance method. The
    // earlier draft invented a same-named instance method to resolve a naming collision that
    // shouldn't have existed in the first place.
    public static void StopMusic()
    {
        AudioManager inst = Instance;
        if (inst == null) return;
        if (inst._music == null) return;
        inst._music.Stop();
    }

    public static void ButtonTap()
    {
        AudioManager inst = Instance;
        inst.PlayClip(inst.buttonTap, inst.buttonVolume);
    }

    public static void PoseChange()
    {
        AudioManager inst = Instance;
        inst.PlayClip(inst.poseChange, inst.poseVolume);
    }

    public static void WaterGunShot()
    {
        AudioManager inst = Instance;
        inst.PlayClip(inst.waterGunShot, inst.gunVolume);
    }

    public static void PaintStroke()
    {
        AudioManager inst = Instance;
        if (inst != null)
        {
            float now = Time.unscaledTime;
            if (inst.paintCooldown <= now - inst._lastPaint)
            {
                // NOTE: confirmed as a SEPARATE, freshly-fetched Time.unscaledTime call here, not
                // a reuse of `now` above — the earlier draft reused `now`.
                float now2 = Time.unscaledTime;
                inst._lastPaint = now2;
                inst.PlayClip(inst.paintStroke, inst.paintVolume);
            }
        }
    }

    public static void PhaseStart()
    {
        AudioManager inst = Instance;
        inst.PlayClip(inst.phaseBellStart, inst.bellVolume);
    }

    public static void PhaseSeek()
    {
        AudioManager inst = Instance;
        inst.PlayClip(inst.phaseBellSeek, inst.bellVolume);
    }

    public static void Win()
    {
        AudioManager inst = Instance;
        inst.PlayClip(inst.winSfx, inst.resultVolume);
    }

    public static void Lose()
    {
        AudioManager inst = Instance;
        inst.PlayClip(inst.loseSfx, inst.resultVolume);
    }

    public static void Countdown(float secondsLeft)
    {
        Instance.CountdownTick(secondsLeft);
    }

    // NOTE: the decompiled infinity-check for secondsLeft is a confusing round-trip
    // (cast-to-int-then-back-to-float compared against Infinity) that reads as effectively
    // always-true/dead code taken literally. Kept as the more sensible `float.IsInfinity` reading
    // instead of the literal (and likely equivalent-in-intent) decompiled form — flagged as
    // inferred, not a certain 1:1 match.
    private void CountdownTick(float secondsLeft)
    {
        int sec = float.IsInfinity(secondsLeft) ? int.MinValue : (int)secondsLeft;

        if (sec != _lastTickSec)
        {
            _lastTickSec = sec;
            if (sec > 0 && sec <= countdownFrom)
            {
                PlayClip(clockTick, clockVolume);
            }
        }
    }

    public static void ResetCountdown()
    {
        AudioManager inst = Instance;
        if (inst != null)
        {
            inst._lastTickSec = -1;
        }
    }
}