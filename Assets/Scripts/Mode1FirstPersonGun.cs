using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Re-verified method by method against raw Ghidra output. Fields (names, order, attributes,
// offsets) and method accessibility match dump.cs (TypeDefIndex 9694). All string literals are
// confirmed against Dumpstringliteral.json.
//
// Decoded from libil2cpp.so (not in the paste):
//  - FUN_02386f6c (LateUpdate, gunAimsForward) = Vector3.ProjectOnPlane: sqrMag = n.n, return v when
//    sqrMag < Mathf.Epsilon, else v - n * dot(v,n) / sqrMag. Called with (playerTf.forward, up).
//  - FUN_023145b0(&v, "F2", 0, 0) = Vector3.ToString(string) -> ToString(format, null).
//  - FUN_02349a84 = Vector3.up, FUN_02349bd8 = Vector3.normalized, FUN_02349b40 = Vector3.Distance.
//
// Inlined accessors that show up in raw as private-field writes: GunHolder.Gun (gun),
// GunHolder.SetForceVisible (forceVisible), CameraController.SetProne (_proneOn),
// CameraController.SetAimPitch (aimPitchDeg), Image.sprite (Ghidra's struct shift prints it as
// m_Corners), CanvasScaler.uiScaleMode / matchWidthOrHeight. The ctor's 8-byte store at
// bulletColor.a is a = 1 plus aimAssist = 2.5 (the next field, 0x144).
//
// Convention: where raw jumps to the NullReferenceException stub, the C# just dereferences
// naturally; explicit null checks are kept only where raw really skips.
public class Mode1FirstPersonGun : MonoBehaviour
{
    public string playerName = "Player";
    private BotHideController hide;
    private GameModeManager gmm;
    private CameraController camCtrl;
    private GunHolder gunHolder;
    private Animator playerAnim;
    private int gunLayer = -1;
    private Font font;
    private GameObject fireBtn;
    private GameObject jumpBtn;
    private GameObject namBtn;
    private bool prone;
    private bool gunModeOn;
    private static readonly int NamHash = Animator.StringToHash("Nam");
    private bool wasGunMode;

    [Header("Aim (Mode 1 — drag the RIGHT side of the screen)")]
    [Tooltip("Yaw degrees per pixel of horizontal drag (turns the player + gun left/right).")]
    public float aimYawSpeed = 0.18f;
    [Tooltip("Pitch degrees per pixel of vertical drag (tilts the aim up/down).")]
    public float aimPitchSpeed = 0.12f;
    [Tooltip("Lowest the aim may tilt (look down).")]
    public float aimMinPitch = -35f;
    [Tooltip("Highest the aim may tilt (look up).")]
    public float aimMaxPitch = 55f;
    [Tooltip("Drag up = aim up. Enable to invert vertical aim.")]
    public bool aimInvertY;
    private GameObject aimZone;
    private GameObject crosshair;
    private Transform playerTf;
    private float aimPitch;

    [Header("Right arm follows the aim (up/down)")]
    [Tooltip("Bone rotated so the gun arm points where you aim. Mixamo upper arm by default.")]
    public string armBoneName = "mixamorig:RightArm";
    [Tooltip("How much the arm pitches per degree of aim pitch (1 = match the crosshair; lower = subtler).")]
    public float armPitchFactor = 1f;
    private Transform rightArm;

    [Header("Gun aim (barrel points at the crosshair)")]
    [Tooltip("Each frame, rotate the held gun so its barrel points exactly at the spot under the crosshair, so the bullet flies straight out of the muzzle toward the crosshair instead of off at an angle.")]
    public bool aimGunAtCrosshair = true;
    [Tooltip("Aim the held gun along the CHARACTER's facing (level forward) instead of the camera crosshair, so the gun does NOT swivel when you only orbit the camera (it turns when the character turns). Bullets still fly to the crosshair.")]
    public bool gunAimsForward;
    [Tooltip("Aim the SHOT and the CROSSHAIR along the GUN BARREL: the reticle moves to where the barrel is pointing, and bullets/damage fly straight out the barrel — so what the gun is aimed at is what you hit. The gun stays where you placed it (aim by turning the character).")]
    public bool aimAlongBarrel = true;
    [Tooltip("Put the crosshair on the REAL impact point — where the shot's ray first meets a wall/character — so the reticle marks exactly where the bullet lands ('what the crosshair is on is what you hit'). Leave ON for accurate aim. Turn OFF to hug the reticle near the gun tip (crosshairAimDistance): tidier, but because the muzzle is offset from the camera a near reticle and the far impact project to DIFFERENT screen spots, so the shot looks like it flies off to the side.")]
    public bool crosshairAtImpact = true;
    [Tooltip("Only used when crosshairAtImpact is OFF. How far ahead of the muzzle (world units) the barrel crosshair sits — SMALLER = the reticle hugs the gun tip more closely. Only moves the on-screen reticle; the bullet/damage still travel the full distance along the barrel.")]
    public float crosshairAimDistance = 3f;
    [Tooltip("Highest the crosshair may sit on screen, as a fraction of screen height (1 = very top). Caps the reticle so it stays BELOW the top HUD (timer / enemy-count icons) instead of climbing into it when you aim forward or up. ONLY moves the shown reticle — the bullet still fires exactly the same way. Lower this to push the reticle further down.")]
    [Range(0.3f, 1f)]
    public float crosshairMaxScreenY01 = 0.78f;
    [Tooltip("Fire along a STABLE aim (the character's facing + your up/down aim pitch) instead of the literal muzzle.forward. The held gun bobs with the hand animation, so muzzle.forward jitters every frame — sampling it at the instant of fire makes consecutive shots scatter in different directions. With this ON every shot goes the same predictable way (and the crosshair tracks it) while the gun still visually sways in the hand. Turn OFF to read the literal swaying barrel again.")]
    public bool aimStabilized = true;
    private Transform gunTf;
    private RectTransform _crosshairRT;
    private RectTransform _crosshairParentRT;
    private readonly RaycastHit[] _aimHits = new RaycastHit[64];

    [Header("Fire button skin (Assets/Texture2D)")]
    [Tooltip("Ring/background behind the gun icon (Ellipse 1 copy 5). Falls back to a red box when unset.")]
    public Sprite fireBgSprite;
    [Tooltip("Gun icon on the fire button (gun.png). Falls back to a \"FIRE\" text label when unset.")]
    public Sprite gunIcon;

    [Header("Prone button (btnnam) sprite based on state")]
    [Tooltip("Sprite when prone (prone enabled) — e.g., 'Layer 26'.")]
    public Sprite namOnSprite;
    [Tooltip("Sprite when STANDING (prone disabled) — e.g., 'Layer 24'. Leave blank = keep the button's original authored sprite.")]
    public Sprite namOffSprite;
    private Image _namBtnImg;

    [Header("Shooting (Seek mode — fires a real projectile)")]
    [Tooltip("Optional bullet prefab. When assigned, THIS prefab is spawned as the bullet — its own look + its Bullet component settings define it. Leave empty to build the glowing-sphere tracer at runtime. Generate one with: Tools ▸ Chameleon ▸ Create Bullet Prefab.")]
    public GameObject bulletPrefab;
    [Tooltip("Bullet travel speed (world units / second).")]
    public float bulletSpeed = 160f;
    [Tooltip("Bullet visual diameter (world units) — the scene is large-scale, so keep it chunky.")]
    public float bulletSize = 1.2f;
    [Tooltip("Seconds before a bullet that hit nothing despawns.")]
    public float bulletLife = 3f;
    public Color bulletColor = new Color(1f, 0.85f, 0.2f, 1f);
    [Tooltip("Aim forgiveness (world units) added around each hider when checking if your shot hits it. Bigger = easier to hit a hider behind furniture; the shot ignores furniture/floor and is only blocked by real walls.")]
    public float aimAssist = 2.5f;

    [Header("Muzzle (fire point at the gun tip)")]
    [Tooltip("Fire point at the barrel tip — bullets spawn here. If unset, one ('MuzzlePoint') is auto-created on the gun at the barrel tip on first fire.")]
    public Transform muzzlePoint;
    [Tooltip("Nudge the auto-created muzzle along the gun's local axes to sit exactly on the barrel tip.")]
    public Vector3 muzzleLocalOffset = Vector3.zero;
    [Tooltip("Bullet flies toward the crosshair (true) or straight along the barrel forward (false).")]
    public bool aimAtCrosshair = true;
    [Tooltip("Fallback muzzle offset from the camera (forward / up) — used only if the gun can't be found.")]
    public float muzzleForward = 2.5f;
    public float muzzleUp = -0.5f;
    private static readonly int GunFireHash = Animator.StringToHash("GunFire");

    private void Start()
    {
        font = UIFont.Default;
        Resolve();
        EnsureEventSystem();
        BuildFireButton();
        Apply(false);
        wasGunMode = false;
    }

    private void Resolve()
    {
        hide = FindFirstObjectByType<BotHideController>();
        gmm = FindFirstObjectByType<GameModeManager>();
        camCtrl = FindFirstObjectByType<CameraController>();
        gunHolder = FindFirstObjectByType<GunHolder>();

        if (bulletPrefab == null)
        {
            GameObject loaded = Resources.Load<GameObject>("Bullet");
            if (loaded != null)
            {
                bulletPrefab = loaded;
            }
        }

        GameObject player = PlayerRef.Resolve(playerName);
        if (player != null)
        {
            playerAnim = player.GetComponentInChildren<Animator>(true);
            playerTf = player.transform;
        }

        rightArm = null;
        if (player != null)
        {
            foreach (Transform t in player.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == armBoneName)
                {
                    rightArm = t;
                    break;
                }
            }
        }

        gunLayer = -1;
        if (playerAnim == null) return;
        for (int i = 0; i < playerAnim.layerCount; i++)
        {
            if (playerAnim.GetLayerName(i) == "Gun")
            {
                gunLayer = i;
                return;
            }
        }
    }

    // Gun mode = round running with the player as the seeker; re-applied only when it flips.
    private void Update()
    {
        bool on = gmm != null && gmm.RoundActive && gmm.PlayerIsSeeker;
        if (wasGunMode != on)
        {
            Apply(on);
            wasGunMode = on;
        }
    }

    private void LateUpdate()
    {
        if (gmm == null) return;
        if (!gmm.RoundActive || !gmm.PlayerIsSeeker) return;
        if (playerTf == null) return;

        // Right arm pitches with the aim (applied after the animator pose).
        if (rightArm != null)
        {
            float armAngle = aimPitch * armPitchFactor;
            if (Mathf.Abs(armAngle) > 0.01f)
            {
                rightArm.Rotate(playerTf.right, -armAngle, Space.World);
            }
        }

        if (aimGunAtCrosshair && !aimAlongBarrel)
        {
            if (gunTf == null && gunHolder != null)
            {
                gunTf = gunHolder.Gun;
            }

            Camera cam = Camera.main;
            if (gunTf != null && cam != null && gunTf.gameObject.activeInHierarchy)
            {
                Vector3 dir = gunAimsForward
                    ? Vector3.ProjectOnPlane(playerTf.forward, Vector3.up)
                    : AimPoint(cam) - gunTf.position;
                if (dir.sqrMagnitude > 0.0001f)
                {
                    gunTf.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
                }
            }
        }

        if (aimAlongBarrel)
        {
            UpdateBarrelCrosshair();
        }
    }

    private void Apply(bool on)
    {
        gunModeOn = on;
        SetProne(false);
        RefreshGunLayerWeight();
        if (camCtrl != null) camCtrl.SetFirstPerson(on);
        if (gunHolder != null) gunHolder.SetForceVisible(on);
        if (fireBtn != null) fireBtn.SetActive(on);
        if (jumpBtn != null) jumpBtn.SetActive(on);
        if (namBtn != null) namBtn.SetActive(on);
        if (aimZone != null) aimZone.SetActive(on);
        if (crosshair != null) crosshair.SetActive(on);
        if (on)
        {
            aimPitch = 0f;
            if (camCtrl != null) camCtrl.SetAimPitch(0f);
        }
    }

    // The "Gun" animator layer is full weight in gun mode, except while prone.
    private void RefreshGunLayerWeight()
    {
        if (playerAnim == null || gunLayer < 0) return;
        playerAnim.SetLayerWeight(gunLayer, (gunModeOn && !prone) ? 1f : 0f);
    }

    public void ToggleProne()
    {
        if (gunModeOn)
        {
            SetProne(!prone);
        }
    }

    private void SetProne(bool v)
    {
        if (prone == v) return;
        prone = v;
        if (playerAnim != null)
        {
            playerAnim.SetBool(NamHash, v);
        }
        RefreshGunLayerWeight();
        if (camCtrl != null)
        {
            camCtrl.SetProne(v);
        }
        if (_namBtnImg != null)
        {
            Sprite s = v ? namOnSprite : namOffSprite;
            if (s != null)
            {
                _namBtnImg.sprite = s;
            }
        }
    }

    public void Fire()
    {
        if (gmm != null && gmm.SeekerHideCountdown) return;

        if (playerAnim != null)
        {
            playerAnim.SetTrigger(GunFireHash);
        }
        AudioManager.WaterGunShot();

        if (gmm == null) return;
        if (!gmm.RoundActive || !gmm.PlayerIsSeeker) return;
        if (hide == null) return;
        Camera cam = Camera.main;
        if (cam == null) return;

        EnsureMuzzle();
        Vector3 origin = muzzlePoint != null
            ? muzzlePoint.position
            : cam.transform.position + cam.transform.forward * muzzleForward + cam.transform.up * muzzleUp;

        // Barrel aim: bullet and hit test both go straight out of the muzzle.
        if (aimAlongBarrel && muzzlePoint != null)
        {
            Vector3 shot = ShotDir();
            SpawnBullet(origin, shot);
            hide.TryShootAim(origin, shot, 1000f, aimAssist);
            return;
        }

        // Crosshair aim: bullet flies from the muzzle to the crosshair point; the hit test uses
        // the camera ray itself.
        CrosshairTarget(cam, out Vector3 point, out GameObject hitGo);
        Vector3 dir = aimAtCrosshair
            ? (point - origin).normalized
            : (muzzlePoint != null ? muzzlePoint : cam.transform).forward;
        if (dir.sqrMagnitude < 1E-06f)
        {
            dir = cam.transform.forward;
        }
        SpawnBullet(origin, dir);
        hide.TryShootAim(cam.transform.position, cam.transform.forward, 1000f, aimAssist);
    }

    private void SpawnBullet(Vector3 origin, Vector3 dir)
    {
        Quaternion rot = Quaternion.LookRotation(dir);
        Bullet bullet;
        if (bulletPrefab != null)
        {
            // Prefab: keep its own Bullet settings, only fill the ones left at 0.
            GameObject go = Instantiate(bulletPrefab, origin, rot);
            go.name = "Bullet";
            bullet = go.GetComponent<Bullet>();
            if (bullet == null)
            {
                bullet = go.AddComponent<Bullet>();
            }
            if (bullet.speed <= 0f) bullet.speed = bulletSpeed;
            if (bullet.maxLife <= 0f) bullet.maxLife = bulletLife;
            if (bullet.radius <= 0f) bullet.radius = Mathf.Max(0.01f, bulletSize * 0.5f);
            if (go.GetComponent<TrailRenderer>() == null)
            {
                AddTracer(go);
            }
        }
        else
        {
            // No prefab: glowing unlit-looking sphere with a trail.
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Bullet";
            Destroy(go.GetComponent<Collider>());
            go.transform.localScale = Vector3.one * bulletSize;
            go.transform.SetPositionAndRotation(origin, rot);

            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Material mat = mr.material;
            mat.color = bulletColor;
            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", bulletColor);
            }
            if (mat.HasProperty("_EmissionColor"))
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", bulletColor * 2.2f);
            }

            TrailRenderer trail = go.AddComponent<TrailRenderer>();
            trail.time = 0.25f;
            trail.startWidth = bulletSize * 0.6f;
            trail.endWidth = 0f;
            trail.material = mat;
            trail.numCapVertices = 2;
            trail.startColor = bulletColor;
            trail.endColor = new Color(bulletColor.r, bulletColor.g, bulletColor.b, 0f);

            bullet = go.AddComponent<Bullet>();
            bullet.speed = bulletSpeed;
            bullet.maxLife = bulletLife;
            bullet.radius = bulletSize * 0.5f;
        }
        bullet.passThroughProps = true;
        bullet.Init(hide, playerTf);
    }

    private void AddTracer(GameObject go)
    {
        TrailRenderer trail = go.AddComponent<TrailRenderer>();
        trail.time = 0.25f;
        trail.startWidth = go.transform.localScale.x * 0.6f;
        trail.endWidth = 0f;
        trail.numCapVertices = 2;

        Material mat = Resources.Load<Material>("BulletTrail");
        if (mat != null)
        {
            trail.sharedMaterial = mat;
        }
        else
        {
            MeshRenderer mr = go.GetComponentInChildren<MeshRenderer>();
            if (mr != null && mr.sharedMaterial != null)
            {
                trail.sharedMaterial = mr.sharedMaterial;
            }
        }
        trail.startColor = bulletColor;
        trail.endColor = new Color(bulletColor.r, bulletColor.g, bulletColor.b, 0f);
    }

    // Finds (or creates at the barrel tip) the "MuzzlePoint" child of the held gun.
    private void EnsureMuzzle()
    {
        Transform gun = gunHolder != null ? gunHolder.Gun : null;
        if (gun == null)
        {
            gun = FindGun();
        }
        if (gun == null) return;

        if (muzzlePoint != null && muzzlePoint.IsChildOf(gun)) return;

        Transform existing = gun.Find("MuzzlePoint");
        if (existing != null)
        {
            muzzlePoint = existing;
            return;
        }

        GameObject go = new GameObject("MuzzlePoint");
        go.transform.SetParent(gun, false);
        go.transform.localRotation = Quaternion.identity;
        go.transform.localPosition = ComputeBarrelTipLocal(gun) + muzzleLocalOffset;
        muzzlePoint = go.transform;
        Debug.Log("[Mode1Gun] Muzzle point created at gun barrel tip (local " + go.transform.localPosition.ToString("F2") + ").");
    }

    private Transform FindGun()
    {
        GameObject go = GameObject.Find("gun");
        if (go != null)
        {
            return go.transform;
        }
        // Also finds an inactive "gun", as long as it is a scene object (not an asset).
        Transform[] all = Resources.FindObjectsOfTypeAll<Transform>();
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t.name == "gun" && t.gameObject.scene.IsValid())
            {
                return t;
            }
        }
        return null;
    }

    // The barrel runs along the gun's longest local axis; the tip is the end of that axis farther
    // from the gun's parent (else the player, else the gun itself).
    private Vector3 ComputeBarrelTipLocal(Transform gun)
    {
        if (!TryLocalBounds(gun, out Bounds b))
        {
            return Vector3.zero;
        }

        Vector3 size = b.size;
        int axis = 0;
        if (size.z >= size.y && size.z >= size.x) axis = 2;
        if (size.y >= size.z && size.y >= size.x) axis = 1;

        Vector3 plus = b.center;
        Vector3 minus = b.center;
        if (axis == 2)
        {
            plus.z = b.center.z + b.extents.z;
            minus.z = b.center.z - b.extents.z;
        }
        else if (axis == 1)
        {
            plus.y = b.center.y + b.extents.y;
            minus.y = b.center.y - b.extents.y;
        }
        else
        {
            plus.x = b.center.x + b.extents.x;
            minus.x = b.center.x - b.extents.x;
        }

        Transform refT = gun.parent != null ? gun.parent : (playerTf != null ? playerTf : gun);
        Vector3 refPos = refT.position;
        float dPlus = Vector3.Distance(gun.TransformPoint(plus), refPos);
        float dMinus = Vector3.Distance(gun.TransformPoint(minus), refPos);
        return dMinus <= dPlus ? plus : minus;
    }

    // Bounds of every child renderer, expressed in the gun's local space (all 8 world-bounds
    // corners transformed and encapsulated). False when the gun has no renderers.
    private bool TryLocalBounds(Transform gun, out Bounds local)
    {
        local = default(Bounds);
        Renderer[] rends = gun.GetComponentsInChildren<Renderer>();
        Matrix4x4 w2l = gun.worldToLocalMatrix;
        bool any = false;
        for (int i = 0; i < rends.Length; i++)
        {
            Vector3 c = rends[i].bounds.center;
            Vector3 e = rends[i].bounds.extents;
            for (int k = 0; k < 8; k++)
            {
                Vector3 corner = c + new Vector3(
                    (k & 1) != 0 ? e.x : -e.x,
                    (k & 2) != 0 ? e.y : -e.y,
                    k > 3 ? e.z : -e.z);
                Vector3 p = w2l.MultiplyPoint3x4(corner);
                if (any)
                {
                    local.Encapsulate(p);
                }
                else
                {
                    local = new Bounds(p, Vector3.zero);
                }
                any = true;
            }
        }
        return rends.Length > 0;
    }

    private Vector3 AimPoint(Camera cam)
    {
        CrosshairTarget(cam, out Vector3 point, out GameObject hitGo);
        return point;
    }

    // Stabilized: the character's facing tilted by the aim pitch. Otherwise the literal muzzle
    // (or camera) forward.
    private Vector3 ShotDir()
    {
        if (aimStabilized && playerTf != null)
        {
            return (Quaternion.AngleAxis(-aimPitch, playerTf.right) * playerTf.forward).normalized;
        }
        Transform src;
        if (muzzlePoint != null)
        {
            src = muzzlePoint;
        }
        else
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                return Vector3.forward;
            }
            src = cam.transform;
        }
        return src.forward;
    }

    // Nearest hit along the camera ray, skipping the player's own colliders and anything that
    // neither is a CharacterController nor Bullet.BlocksShot. Nothing hit = 200 units ahead.
    private void CrosshairTarget(Camera cam, out Vector3 point, out GameObject hitGo)
    {
        Ray ray = new Ray(cam.transform.position, cam.transform.forward);
        int count = Physics.RaycastNonAlloc(ray, _aimHits, 1000f);
        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            Collider col = _aimHits[i].collider;
            Transform t = col.transform;
            if (playerTf != null && (t == playerTf || t.IsChildOf(playerTf))) continue;
            if (!(col is CharacterController) && !Bullet.BlocksShot(col.gameObject)) continue;
            if (_aimHits[i].distance < bestDist)
            {
                bestDist = _aimHits[i].distance;
                best = i;
            }
        }
        if (best >= 0)
        {
            point = _aimHits[best].point;
            hitGo = _aimHits[best].collider.gameObject;
            return;
        }
        point = ray.GetPoint(200f);
        hitGo = null;
    }

    // Moves the crosshair to where the barrel shot lands (or crosshairAimDistance ahead of the
    // muzzle), capped below crosshairMaxScreenY01 of the screen height.
    private void UpdateBarrelCrosshair()
    {
        EnsureMuzzle();
        if (muzzlePoint == null) return;
        if (crosshair == null) return;
        Camera cam = Camera.main;
        if (cam == null) return;

        if (_crosshairRT == null)
        {
            _crosshairRT = crosshair.transform as RectTransform;
        }
        if (_crosshairParentRT == null)
        {
            _crosshairParentRT = crosshair.transform.parent as RectTransform;
        }
        if (_crosshairRT == null) return;
        if (_crosshairParentRT == null) return;

        Vector3 target = BarrelAimPoint(out GameObject hitGo);
        if (!crosshairAtImpact && Vector3.Distance(muzzlePoint.position, target) > crosshairAimDistance)
        {
            target = muzzlePoint.position + ShotDir() * crosshairAimDistance;
        }

        Vector3 sp = cam.WorldToScreenPoint(target);
        if (sp.z > 0f)
        {
            float maxY = Mathf.Clamp01(crosshairMaxScreenY01) * Screen.height;
            Vector2 screen = new Vector2(sp.x, Mathf.Min(sp.y, maxY));
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_crosshairParentRT, screen, null, out Vector2 localPos))
            {
                _crosshairRT.anchoredPosition = localPos;
            }
        }
    }

    // Same filter as CrosshairTarget, but along ShotDir() from the muzzle.
    private Vector3 BarrelAimPoint(out GameObject hitGo)
    {
        hitGo = null;
        Ray ray = new Ray(muzzlePoint.position, ShotDir());
        int count = Physics.RaycastNonAlloc(ray, _aimHits, 1000f);
        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            Collider col = _aimHits[i].collider;
            Transform t = col.transform;
            if (playerTf != null && (t == playerTf || t.IsChildOf(playerTf))) continue;
            if (!(col is CharacterController) && !Bullet.BlocksShot(col.gameObject)) continue;
            if (_aimHits[i].distance < bestDist)
            {
                bestDist = _aimHits[i].distance;
                best = i;
            }
        }
        if (best >= 0)
        {
            hitGo = _aimHits[best].collider.gameObject;
            return _aimHits[best].point;
        }
        return ray.GetPoint(200f);
    }

    public void AuthorUI()
    {
        if (font == null)
        {
            font = UIFont.Default;
        }
        if (hide == null)
        {
            Resolve();
        }
        EnsureEventSystem();
        BuildFireButton();
    }

    // "FireButtonCanvas" (sort 8, 1080x1920): the aim zone, "FireBtn" (skinned or red fallback)
    // with its "GunIcon" / "L" label, the authored "Jump" and "btnnam" buttons, and the crosshair.
    private void BuildFireButton()
    {
        GameObject canvasGO = SceneUI.GetOrCreateCanvas("FireButtonCanvas", out bool canvasCreated);
        Canvas canvas = SceneUI.GetOrAdd<Canvas>(canvasGO);
        if (canvasCreated)
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 8;
            CanvasScaler scaler = SceneUI.GetOrAdd<CanvasScaler>(canvasGO);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 1f;
        }
        SceneUI.GetOrAdd<GraphicRaycaster>(canvasGO);
        BuildAimZone();

        GameObject btnGO = SceneUI.GetOrCreate("FireBtn", canvasGO.transform, out bool btnCreated);
        Image bg = SceneUI.GetOrAdd<Image>(btnGO);
        if (fireBgSprite != null)
        {
            bg.sprite = fireBgSprite;
            bg.color = Color.white;
            bg.preserveAspect = true;
            RectTransform rt = bg.rectTransform;
            if (btnCreated)
            {
                rt.anchorMax = new Vector2(1f, 0.5f);
                rt.anchorMin = new Vector2(1f, 0.5f);
                rt.pivot = new Vector2(1f, 0.5f);
                rt.anchoredPosition = new Vector2(-110f, 20f);
                rt.sizeDelta = new Vector2(180f, 180f);
            }
        }
        else
        {
            bg.sprite = null;
            bg.color = new Color(0.85f, 0.25f, 0.2f, 0.95f);
            RectTransform rt = bg.rectTransform;
            if (btnCreated)
            {
                rt.anchorMax = new Vector2(1f, 0f);
                rt.anchorMin = new Vector2(1f, 0f);
                rt.pivot = new Vector2(1f, 0f);
                rt.anchoredPosition = new Vector2(-60f, 70f);
                rt.sizeDelta = new Vector2(190f, 190f);
            }
        }
        Button btn = SceneUI.GetOrAdd<Button>(btnGO);
        btn.targetGraphic = bg;

        if (gunIcon != null)
        {
            GameObject iconGO = SceneUI.GetOrCreate("GunIcon", btnGO.transform, out bool iconCreated);
            Image icon = SceneUI.GetOrAdd<Image>(iconGO);
            icon.sprite = gunIcon;
            icon.color = Color.white;
            icon.raycastTarget = false;
            icon.preserveAspect = true;
            RectTransform irt = icon.rectTransform;
            irt.anchorMax = new Vector2(0.5f, 0.5f);
            irt.anchorMin = new Vector2(0.5f, 0.5f);
            irt.pivot = new Vector2(0.5f, 0.5f);
            irt.anchoredPosition = Vector2.zero;
            irt.sizeDelta = new Vector2(120f, 100f);
        }

        GameObject labelGO = SceneUI.GetOrCreate("L", btnGO.transform, out bool labelCreated);
        Text label = SceneUI.GetOrAdd<Text>(labelGO);
        label.font = font;
        label.raycastTarget = false;
        if (labelCreated)
        {
            label.text = "FIRE";
            label.fontSize = 46;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            RectTransform lrt = label.rectTransform;
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero;
            lrt.offsetMax = Vector2.zero;
        }
        // The text label is only the fallback for a missing gun icon.
        label.gameObject.SetActive(gunIcon == null);
        fireBtn = btnGO;

        Transform jump = canvasGO.transform.Find("Jump");
        jumpBtn = jump != null ? jump.gameObject : null;
        Transform nam = canvasGO.transform.Find("btnnam");
        namBtn = nam != null ? nam.gameObject : null;
        _namBtnImg = namBtn != null ? namBtn.GetComponent<Image>() : null;
        // No "standing" sprite assigned: keep the button's authored sprite for it.
        if (namOffSprite == null && _namBtnImg != null)
        {
            namOffSprite = _namBtnImg.sprite;
        }

        BuildCrosshair(canvasGO.transform);
    }

    // "Mode1AimCanvas" (sort 4, 1080x1920) with a full-screen invisible "AimZone" whose drags aim.
    private void BuildAimZone()
    {
        GameObject canvasGO = SceneUI.GetOrCreateCanvas("Mode1AimCanvas", out bool canvasCreated);
        Canvas canvas = SceneUI.GetOrAdd<Canvas>(canvasGO);
        if (canvasCreated)
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 4;
            CanvasScaler scaler = SceneUI.GetOrAdd<CanvasScaler>(canvasGO);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 1f;
        }
        SceneUI.GetOrAdd<GraphicRaycaster>(canvasGO);

        GameObject zone = SceneUI.GetOrCreate("AimZone", canvasGO.transform, out bool zoneCreated);
        Image img = SceneUI.GetOrAdd<Image>(zone);
        img.color = Color.clear;
        img.raycastTarget = true;
        RectTransform rt = img.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        UIDrag drag = SceneUI.GetOrAdd<UIDrag>(zone);
        drag.cb = OnAimDrag;
        aimZone = zone;
    }

    // Horizontal drag turns the player; vertical drag changes the (clamped) aim pitch.
    private void OnAimDrag(PointerEventData e)
    {
        if (gmm != null && gmm.SeekerHideCountdown) return;

        if (playerTf != null)
        {
            float dx = e.delta.x;
            if (Mathf.Abs(dx) > 0.0001f)
            {
                playerTf.Rotate(0f, dx * aimYawSpeed, 0f);
            }
        }

        float dy = aimInvertY ? -e.delta.y : e.delta.y;
        aimPitch = Mathf.Clamp(aimPitch + dy * aimPitchSpeed, aimMinPitch, aimMaxPitch);
        if (camCtrl != null)
        {
            camCtrl.SetAimPitch(aimPitch);
        }
    }

    // 64x64 "Crosshair" at screen centre: four bars and a centre dot.
    private void BuildCrosshair(Transform canvas)
    {
        GameObject go = SceneUI.GetOrCreate("Crosshair", canvas, out bool created);
        RectTransform rt = SceneUI.GetOrAdd<RectTransform>(go);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(64f, 64f);

        CrosshairBar(go.transform, "Up", new Vector2(0f, 18f), new Vector2(4f, 18f), new Color(1f, 1f, 1f, 0.85f));
        CrosshairBar(go.transform, "Down", new Vector2(0f, -18f), new Vector2(4f, 18f), new Color(1f, 1f, 1f, 0.85f));
        CrosshairBar(go.transform, "Left", new Vector2(-18f, 0f), new Vector2(18f, 4f), new Color(1f, 1f, 1f, 0.85f));
        CrosshairBar(go.transform, "Right", new Vector2(18f, 0f), new Vector2(18f, 4f), new Color(1f, 1f, 1f, 0.85f));
        CrosshairBar(go.transform, "Dot", Vector2.zero, new Vector2(6f, 6f), new Color(1f, 1f, 1f, 0.85f));
        crosshair = go;
    }

    // One bar = a dark outline image 3 units larger, with the coloured fill "F" inset 1.5 inside.
    private void CrosshairBar(Transform parent, string name, Vector2 pos, Vector2 size, Color col)
    {
        GameObject go = SceneUI.GetOrCreate(name, parent, out bool created);
        Image outline = SceneUI.GetOrAdd<Image>(go);
        outline.color = new Color(0f, 0f, 0f, 0.65f);
        outline.raycastTarget = false;
        RectTransform rt = outline.rectTransform;
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = new Vector2(size.x + 3f, size.y + 3f);

        GameObject fillGO = SceneUI.GetOrCreate("F", go.transform, out bool fillCreated);
        Image fill = SceneUI.GetOrAdd<Image>(fillGO);
        fill.color = col;
        fill.raycastTarget = false;
        RectTransform frt = fill.rectTransform;
        frt.anchorMin = Vector2.zero;
        frt.anchorMax = Vector2.one;
        frt.offsetMin = new Vector2(1.5f, 1.5f);
        frt.offsetMax = new Vector2(-1.5f, -1.5f);
    }

    // Creates an EventSystem if none exists: new Input System UI module when that package is
    // present (looked up by name), else StandaloneInputModule.
    private void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null) return;

        GameObject go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();
        Type inputModule = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
        if (inputModule != null)
        {
            go.AddComponent(inputModule);
            return;
        }
        go.AddComponent<StandaloneInputModule>();
    }
}
