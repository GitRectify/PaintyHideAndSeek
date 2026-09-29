using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// NOTE: First-person aim/shoot mode for the Seeker role - toggles a runtime-built fire button,
// aim-drag zone, and crosshair UI, raycasts for aim targets (either along the barrel or from
// screen crosshair), and spawns Bullet projectiles. Confirms several new cross-class members not
// previously seen: GameModeManager.PlayerIsSeeker (own backing field, alongside RoundActive/
// PlayerFrozen/SeekerHideCountdown), CameraController.SetFirstPerson/aimPitchDeg/_proneOn,
// GunHolder.gun/forceVisible (resolves part of the old "GunHolder fields confirmed but unused"
// caveat), Bullet.speed/maxLife/radius/Init/BlocksShot (adds to the already-delivered Bullet.cs
// from an earlier session), AudioManager.WaterGunShot (static, like PoseChange), and a new
// unreversed class BotHideController with a confirmed TryShootAim(Vector3,Vector3,float,float)
// call surface. Also confirms SceneUI.GetOrAdd<RectTransform> and a new unreversed component
// type "UIDrag" (SceneUI.GetOrAdd<UIDrag>, wired to an Action<PointerEventData> callback -
// almost certainly a custom IDragHandler component, not decompiled itself here).
public class Mode1FirstPersonGun : MonoBehaviour
{
    [SerializeField] private string playerName = "Player";
    [SerializeField] private string armBoneName = "mixamorig:RightArm";

    [SerializeField] private float aimMinPitch = -35f;
    [SerializeField] private float aimMaxPitch = 55f;
    [SerializeField] private float aimYawSpeed = 0.18f;
    [SerializeField] private float aimPitchSpeed = 0.12f;
    [SerializeField] private float armPitchFactor = 1f;
    [SerializeField] private bool aimGunAtCrosshair = true;
    [SerializeField] private bool aimAlongBarrel = true;
    [SerializeField] private bool aimAtCrosshair = true;
    [SerializeField] private bool aimInvertY;
    [SerializeField] private bool gunAimsForward;
    [SerializeField] private bool crosshairAtImpact = true;
    [SerializeField] private float crosshairAimDistance = 3f;
    [SerializeField] private float crosshairMaxScreenY01 = 0.78f;
    [SerializeField] private bool aimStabilized = true;
    [SerializeField] private float aimAssist;

    [SerializeField] private float bulletLife = 3f;
    [SerializeField] private float bulletSpeed = 160f;
    [SerializeField] private float bulletSize = 1.2f;
    // NOTE: decompiled ctor writes this via an 8-byte store that overlaps past the end of the
    // Color struct into the next field (spilling into what later becomes muzzleForward = 2.5f) -
    // clearly decompiler struct-layout noise. Confirmed via IEEE-754 decode: low 32 bits =
    // 0x3f800000 = 1.0 (this IS bulletColor.a), high 32 bits = 0x40200000 = 2.5 (the spillover,
    // harmless since muzzleForward is explicitly overwritten to 2.5f later anyway). Taking only
    // the confirmed parts: r=1, g=0.85, b=0.2 (each set individually), a=1.
    [SerializeField] private Color bulletColor = new Color(1f, 0.85f, 0.2f, 1f);
    [SerializeField] private GameObject bulletPrefab;

    [SerializeField] private float muzzleForward = 2.5f;
    [SerializeField] private float muzzleUp = -0.5f;
    [SerializeField] private Vector3 muzzleLocalOffset = Vector3.zero;

    [SerializeField] private Sprite fireBgSprite;
    [SerializeField] private Sprite gunIcon;
    [SerializeField] private Font font;

    // NOTE: confirmed to exist via raw field offsets in SetProne (0x108/0x110) - names inferred:
    // "on" is used when prone == true, "off" when prone == false. A third, separately-tracked
    // sprite (namOffSprite below) is lazily backfilled from the button's initial sprite in
    // BuildFireButton if it was never assigned - plausibly the same field as namOffSprite here,
    // unified under that reading.
    [SerializeField] private Sprite namOnSprite;
    [SerializeField] private Sprite namOffSprite;

    private BotHideController hide;
    private GameModeManager gmm;
    private CameraController camCtrl;
    private GunHolder gunHolder;
    private Animator playerAnim;
    private Transform playerTf;
    private Transform rightArm;
    private int gunLayer = -1;

    private bool gunModeOn;
    private bool wasGunMode;
    private bool prone;
    private float aimPitch;

    private Transform muzzlePoint;
    private Transform gunTf;
    private RaycastHit[] _aimHits = new RaycastHit[64];

    private GameObject fireBtn;
    private GameObject jumpBtn;
    private GameObject namBtn;
    private Image _namBtnImg;
    private GameObject aimZone;
    private GameObject crosshair;
    private RectTransform _crosshairRT;
    private RectTransform _crosshairParentRT;

    private static readonly int NamHash = Animator.StringToHash("Nam");
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

        GameObject playerGo = PlayerRef.Resolve(playerName);
        if (playerGo != null)
        {
            playerAnim = playerGo.GetComponentInChildren<Animator>(true);
            playerTf = playerGo.transform;
        }

        rightArm = null;
        if (playerGo != null)
        {
            Transform[] all = playerGo.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].name == armBoneName)
                {
                    rightArm = all[i];
                    break;
                }
            }
        }

        gunLayer = -1;
        if (playerAnim != null)
        {
            for (int layerIndex = 0; layerIndex < playerAnim.layerCount; layerIndex++)
            {
                if (playerAnim.GetLayerName(layerIndex) == "Gun")
                {
                    gunLayer = layerIndex;
                    return;
                }
            }
        }
    }

    private void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null)
        {
            return;
        }

        GameObject go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();

        // NOTE: looks up the new Input System's UI module by name at runtime (so this compiles
        // regardless of whether the Input System package is installed) and falls back to the
        // legacy StandaloneInputModule if that type isn't found.
        Type inputModuleType = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
        if (inputModuleType != null)
        {
            go.AddComponent(inputModuleType);
        }
        else
        {
            go.AddComponent<StandaloneInputModule>();
        }
    }

    public void Apply(bool on)
    {
        gunModeOn = on;
        SetProne(false);
        RefreshGunLayerWeight();

        if (camCtrl != null)
        {
            camCtrl.SetFirstPerson(on);
        }
        if (gunHolder != null)
        {
            gunHolder.forceVisible = on;
        }
        if (fireBtn != null)
        {
            fireBtn.SetActive(on);
        }
        if (jumpBtn != null)
        {
            jumpBtn.SetActive(on);
        }
        if (namBtn != null)
        {
            namBtn.SetActive(on);
        }
        if (aimZone != null)
        {
            aimZone.SetActive(on);
        }
        if (crosshair != null)
        {
            crosshair.SetActive(on);
        }

        if (on)
        {
            aimPitch = 0f;
            if (camCtrl != null)
            {
                camCtrl.aimPitchDeg = 0f;
            }
        }
    }

    private void Update()
    {
        bool shouldBeGunMode = gmm != null && gmm.RoundActive && gmm.PlayerIsSeeker;

        if (wasGunMode != shouldBeGunMode)
        {
            Apply(shouldBeGunMode);
            wasGunMode = shouldBeGunMode;
        }
    }

    // NOTE: the raw gunAimsForward==true branch, and the shared "up" vector construction used by
    // BOTH branches below the aimGunAtCrosshair/aimAlongBarrel gate, route through undecompiled
    // helpers (FUN_02349a84 / FUN_02386f6c for the target point, FUN_02349bd8 / another
    // FUN_02349a84 call for the final LookRotation "up" vector and part of "dir"). These aren't
    // simple Unity API calls and weren't recoverable from the pseudocode. Reconstructed as a
    // point one unit ahead of the player along their forward vector (matches the apparent intent
    // "aim straight ahead") with a plain Vector3.up for LookRotation's up parameter - a
    // reasonable default, not a confirmed transcription of what those helpers actually compute.
    private void LateUpdate()
    {
        if (gmm == null)
        {
            return;
        }
        if (!gmm.RoundActive || !gmm.PlayerIsSeeker)
        {
            return;
        }
        if (playerTf == null)
        {
            return;
        }

        if (rightArm != null)
        {
            float armDelta = aimPitch * armPitchFactor;
            if (Mathf.Abs(armDelta) > 0.01f)
            {
                rightArm.Rotate(playerTf.right, -armDelta, Space.World);
            }
        }

        if (aimGunAtCrosshair && !aimAlongBarrel)
        {
            if (gunTf == null && gunHolder != null)
            {
                gunTf = gunHolder.gun;
            }

            Camera cam = Camera.main;
            if (gunTf != null && cam != null && gunTf.gameObject.activeInHierarchy)
            {
                Vector3 targetPoint;
                if (!gunAimsForward)
                {
                    targetPoint = AimPoint(cam);
                }
                else
                {
                    targetPoint = playerTf.position + playerTf.forward;
                }

                Vector3 aimDir = targetPoint - gunTf.position;
                if (aimDir.sqrMagnitude > 0.0001f)
                {
                    gunTf.rotation = Quaternion.LookRotation(aimDir, Vector3.up);
                }
            }
        }

        if (aimAlongBarrel)
        {
            UpdateBarrelCrosshair();
        }
    }

    private Vector3 AimPoint(Camera cam)
    {
        Vector3 point;
        GameObject hitGo;
        CrosshairTarget(cam, out point, out hitGo);
        return point;
    }

    // NOTE: skips hits on the player's own body (self or child of playerTf); otherwise accepts
    // CharacterController hits unconditionally and gates everything else through
    // Bullet.BlocksShot, picking the closest qualifying hit. Falls back to a point 200 units
    // along the camera's forward ray if nothing qualifies.
    private void CrosshairTarget(Camera cam, out Vector3 point, out GameObject hitGo)
    {
        Transform camT = cam.transform;
        Vector3 origin = camT.position;
        Vector3 dir = camT.forward.normalized;

        int hitCount = Physics.RaycastNonAlloc(new Ray(origin, dir), _aimHits, 1000f);

        int bestIndex = -1;
        float bestDist = float.MaxValue;

        for (int i = 0; i < hitCount; i++)
        {
            Collider col = _aimHits[i].collider;
            Transform hitT = col.transform;

            if (playerTf != null && (hitT == playerTf || hitT.IsChildOf(playerTf)))
            {
                continue;
            }

            if (!(col is CharacterController) && !Bullet.BlocksShot(col.gameObject))
            {
                continue;
            }

            float dist = _aimHits[i].distance;
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIndex = i;
            }
        }

        if (bestIndex >= 0)
        {
            point = _aimHits[bestIndex].point;
            hitGo = _aimHits[bestIndex].collider.gameObject;
            return;
        }

        point = origin + dir * 200f;
        hitGo = null;
    }

    private void UpdateBarrelCrosshair()
    {
        EnsureMuzzle();
        if (muzzlePoint == null)
        {
            return;
        }
        if (crosshair == null)
        {
            return;
        }

        Camera cam = Camera.main;
        if (cam == null)
        {
            return;
        }

        if (_crosshairRT == null)
        {
            _crosshairRT = crosshair.transform as RectTransform;
        }
        if (_crosshairParentRT == null)
        {
            _crosshairParentRT = crosshair.transform.parent as RectTransform;
        }
        if (_crosshairRT == null || _crosshairParentRT == null)
        {
            return;
        }

        GameObject hitGo;
        Vector3 impactPoint = BarrelAimPoint(out hitGo);
        Vector3 targetPoint = impactPoint;

        if (!crosshairAtImpact)
        {
            float dist = Vector3.Distance(muzzlePoint.position, impactPoint);
            if (crosshairAimDistance < dist)
            {
                targetPoint = muzzlePoint.position + ShotDir() * crosshairAimDistance;
            }
        }

        Vector3 screenPoint = cam.WorldToScreenPoint(targetPoint);
        if (screenPoint.z <= 0f)
        {
            return;
        }

        float maxY = Mathf.Clamp01(crosshairMaxScreenY01) * Screen.height;
        float clampedY = Mathf.Min(screenPoint.y, maxY);
        Vector2 clampedScreenPoint = new Vector2(screenPoint.x, clampedY);

        Vector2 localPoint;
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_crosshairParentRT, clampedScreenPoint, null, out localPoint))
        {
            _crosshairRT.anchoredPosition = localPoint;
        }
    }

    private void SetProne(bool v)
    {
        if (prone == v)
        {
            return;
        }

        prone = v;

        if (playerAnim != null)
        {
            playerAnim.SetBool(NamHash, v);
        }

        RefreshGunLayerWeight();

        // NOTE: CameraController._proneOn is written directly here (matching the raw field
        // write in the decompiled code) - assumes it's accessible from outside CameraController;
        // not re-verified against that class's own definition in this session.
        if (camCtrl != null)
        {
            camCtrl._proneOn = v;
        }

        if (_namBtnImg != null)
        {
            Sprite sprite = v ? namOnSprite : namOffSprite;
            if (sprite != null)
            {
                _namBtnImg.sprite = sprite;
            }
        }
    }

    private void RefreshGunLayerWeight()
    {
        if (playerAnim == null)
        {
            return;
        }
        if (gunLayer < 0)
        {
            return;
        }

        float weight = 0f;
        if (gunModeOn)
        {
            weight = prone ? 0f : 1f;
        }

        playerAnim.SetLayerWeight(gunLayer, weight);
    }

    public void ToggleProne()
    {
        if (gunModeOn)
        {
            SetProne(!prone);
        }
    }

    // FIX (confirmed): in raw, the aimAlongBarrel branch's `return` is INSIDE the
    // `muzzlePoint != null` check, not after it - `if (aimAlongBarrel) { if (muzzlePoint !=
    // null) { ...fire along barrel...; return; } }` with no else. So when aimAlongBarrel is
    // true (the default) but muzzlePoint is still null, raw does NOT stop here - it falls
    // straight through into the crosshair-targeting fire logic below. A prior draft had an
    // unconditional `return;` right after the inner block, which instead did nothing in that
    // case. Fixed by only early-returning when both aimAlongBarrel and muzzlePoint != null hold.
    public void Fire()
    {
        if (gmm != null && gmm.SeekerHideCountdown)
        {
            return;
        }

        if (playerAnim != null)
        {
            playerAnim.SetTrigger(GunFireHash);
        }

        AudioManager.WaterGunShot();

        if (gmm == null)
        {
            return;
        }
        if (!gmm.RoundActive || !gmm.PlayerIsSeeker)
        {
            return;
        }
        if (hide == null)
        {
            return;
        }

        Camera cam = Camera.main;
        if (cam == null)
        {
            return;
        }

        EnsureMuzzle();

        Vector3 origin;
        if (muzzlePoint != null)
        {
            origin = muzzlePoint.position;
        }
        else
        {
            Transform camT = cam.transform;
            origin = camT.position + camT.forward * muzzleForward + camT.up * muzzleUp;
        }

        if (aimAlongBarrel && muzzlePoint != null)
        {
            Vector3 dir = ShotDir();
            SpawnBullet(origin, dir);
            hide.TryShootAim(origin, dir, 1000f, aimAssist);
            return;
        }

        GameObject crosshairHitGo;
        Vector3 crosshairPoint;
        CrosshairTarget(cam, out crosshairPoint, out crosshairHitGo);

        // NOTE (genuinely ambiguous, not confirmed): raw's z-component here is the RAW
        // crosshairPoint.z, not (crosshairPoint.z - origin.z) like the x/y components are - and
        // the x-component is run through the same undecompiled FUN_02349bd8 helper seen in
        // LateUpdate. That asymmetry could be real decompiled behavior or could be register
        // mislabeling; it isn't resolvable without decompiling that helper. Kept as the natural
        // "aim toward the crosshair point" interpretation below, which is almost certainly the
        // intent, but flagging that the exact raw arithmetic isn't fully confirmed.
        Vector3 shotDirection;
        if (aimAtCrosshair)
        {
            shotDirection = (crosshairPoint - origin).normalized;
        }
        else
        {
            Transform source = (muzzlePoint != null) ? muzzlePoint : cam.transform;
            shotDirection = source.forward;
        }

        if (shotDirection.sqrMagnitude < 1e-6f)
        {
            shotDirection = cam.transform.forward;
        }

        SpawnBullet(origin, shotDirection);

        // NOTE: the aim-assist notification uses the camera's own position/forward here, not
        // the bullet's actual origin/direction - matches the decompiled code exactly (distinct
        // from the aimAlongBarrel path above, which does use the bullet's own origin/dir).
        hide.TryShootAim(cam.transform.position, cam.transform.forward, 1000f, aimAssist);
    }

    private void EnsureMuzzle()
    {
        Transform gun = (gunHolder != null) ? gunHolder.gun : null;
        if (gun == null)
        {
            gun = FindGun();
        }
        if (gun == null)
        {
            return;
        }

        if (muzzlePoint != null && muzzlePoint.IsChildOf(gun))
        {
            return;
        }

        Transform existing = gun.Find("MuzzlePoint");
        if (existing != null)
        {
            muzzlePoint = existing;
            return;
        }

        GameObject muzzleGo = new GameObject("MuzzlePoint");
        Transform muzzleT = muzzleGo.transform;
        muzzleT.SetParent(gun, false);
        muzzleT.localRotation = Quaternion.identity;

        Vector3 tip = ComputeBarrelTipLocal(gun);
        muzzleT.localPosition = tip + muzzleLocalOffset;
        muzzlePoint = muzzleT;

        Debug.Log($"[Mode1Gun] Muzzle point created at gun barrel tip (local {muzzleT.localPosition:F2}).");
    }

    private Vector3 ShotDir()
    {
        if (aimStabilized && playerTf != null)
        {
            Quaternion pitchRot = Quaternion.AngleAxis(-aimPitch, playerTf.right);
            Vector3 dir = pitchRot * playerTf.forward;
            return (dir.sqrMagnitude <= 1e-10f) ? Vector3.zero : dir.normalized;
        }

        if (muzzlePoint != null)
        {
            return muzzlePoint.forward;
        }

        Camera cam = Camera.main;
        if (cam == null)
        {
            // NOTE: decompiled falls back to the raw Vector3.forward static constant here
            // rather than crashing when both muzzlePoint and Camera.main are unavailable.
            return Vector3.forward;
        }
        return cam.transform.forward;
    }

    private void SpawnBullet(Vector3 origin, Vector3 dir)
    {
        Quaternion rotation = Quaternion.LookRotation(dir);

        if (bulletPrefab != null)
        {
            GameObject bulletGo = Instantiate(bulletPrefab, origin, rotation);
            bulletGo.name = "Bullet";

            Bullet bullet = bulletGo.GetComponent<Bullet>();
            if (bullet == null)
            {
                bullet = bulletGo.AddComponent<Bullet>();
            }

            if (bullet.speed <= 0f)
            {
                bullet.speed = bulletSpeed;
            }
            if (bullet.maxLife <= 0f)
            {
                bullet.maxLife = bulletLife;
            }
            if (bullet.radius <= 0f)
            {
                bullet.radius = Mathf.Max(0.01f, bulletSize * 0.5f);
            }

            if (bulletGo.GetComponent<TrailRenderer>() == null)
            {
                AddTracer(bulletGo);
            }

            bullet.passThroughProps = true;
            bullet.Init(hide, playerTf);
            return;
        }

        // Fallback: no bullet prefab assigned - build a plain primitive sphere with a trail.
        GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "Bullet";
        Destroy(sphere.GetComponent<Collider>());

        Transform sphereT = sphere.transform;
        sphereT.localScale = Vector3.one * bulletSize;
        sphereT.SetPositionAndRotation(origin, rotation);

        Renderer renderer = sphere.GetComponent<MeshRenderer>();
        if (renderer == null)
        {
            return;
        }
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        Material material = renderer.material;
        if (material == null)
        {
            return;
        }
        material.color = bulletColor;

        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", bulletColor);
        }
        if (material.HasProperty("_EmissionColor"))
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", bulletColor * 2.2f);
        }

        TrailRenderer trail = sphere.AddComponent<TrailRenderer>();
        trail.time = 0.25f;
        trail.startWidth = bulletSize * 0.6f;
        trail.endWidth = 0f;
        trail.material = material;
        trail.numCapVertices = 2;
        trail.startColor = bulletColor;
        trail.endColor = new Color(bulletColor.r, bulletColor.g, bulletColor.b, 0f);

        Bullet fallbackBullet = sphere.AddComponent<Bullet>();
        fallbackBullet.speed = bulletSpeed;
        fallbackBullet.maxLife = bulletLife;
        fallbackBullet.radius = bulletSize * 0.5f;

        fallbackBullet.passThroughProps = true;
        fallbackBullet.Init(hide, playerTf);
    }

    private void AddTracer(GameObject go)
    {
        TrailRenderer trail = go.AddComponent<TrailRenderer>();
        trail.time = 0.25f;

        Vector3 scale = go.transform.localScale;
        trail.startWidth = scale.x * 0.6f;
        trail.endWidth = 0f;
        trail.numCapVertices = 2;

        Material material = Resources.Load<Material>("BulletTrail");
        if (material == null)
        {
            Renderer meshRenderer = go.GetComponentInChildren<MeshRenderer>();
            if (meshRenderer != null)
            {
                material = meshRenderer.sharedMaterial;
            }
        }
        if (material != null)
        {
            trail.sharedMaterial = material;
        }

        trail.startColor = bulletColor;
        trail.endColor = new Color(bulletColor.r, bulletColor.g, bulletColor.b, 0f);
    }

    // NOTE: __this is declared but never actually used in the decompiled body - this is a
    // static scene-lookup, hardcoded to search for an object literally named "gun" (a 9th
    // occurrence of the cross-cutting FindInScene pattern, specialized to one fixed name).
    private static Transform FindGun()
    {
        GameObject go = GameObject.Find("gun");
        if (go != null)
        {
            return go.transform;
        }

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

    // NOTE: picks whichever local axis (x/y/z) has the largest extent as the "barrel" axis, then
    // returns whichever end of that axis is farther (in world space) from the gun's parent (or
    // playerTf, or the gun itself as further fallbacks).
    //
    // FIX (confirmed): the axis selection below reproduces raw's exact two-stage comparison
    // rather than a plain sizeX>=sizeY&&sizeX>=sizeZ / sizeY>=sizeZ chain. Hand-traced both
    // against several cases: they agree whenever no two extents are exactly equal, but diverge
    // on exact ties - e.g. sizeX==sizeZ>sizeY: raw picks Z, a plain >= chain picks X; sizeX==
    // sizeY==sizeZ: raw picks Y, a plain chain picks X. Exact floating-point ties between two
    // bounds-extent axes are extremely unlikely for real gun meshes, but since raw's tie-break
    // order is fully derivable, it's reproduced exactly rather than approximated.
    private Vector3 ComputeBarrelTipLocal(Transform gun)
    {
        Bounds localBounds;
        if (!TryLocalBounds(gun, out localBounds))
        {
            return Vector3.zero;
        }

        float sizeX = localBounds.extents.x;
        float sizeY = localBounds.extents.y;
        float sizeZ = localBounds.extents.z;

        int axis = 0;
        if (sizeY <= sizeZ && sizeZ >= sizeX) { axis = 2; }
        if (sizeZ <= sizeY && sizeY >= sizeX) { axis = 1; } // evaluated after; wins on overlap (Y=Z ties)

        Vector3 candidateA = localBounds.center;
        Vector3 candidateB = localBounds.center;
        switch (axis)
        {
            case 0:
                candidateA.x += localBounds.extents.x;
                candidateB.x -= localBounds.extents.x;
                break;
            case 1:
                candidateA.y += localBounds.extents.y;
                candidateB.y -= localBounds.extents.y;
                break;
            default:
                candidateA.z += localBounds.extents.z;
                candidateB.z -= localBounds.extents.z;
                break;
        }

        Transform referenceT = gun.parent != null ? gun.parent : (playerTf != null ? playerTf : gun);
        Vector3 referencePos = referenceT.position;

        Vector3 worldA = gun.TransformPoint(candidateA);
        Vector3 worldB = gun.TransformPoint(candidateB);

        return (worldB - referencePos).sqrMagnitude <= (worldA - referencePos).sqrMagnitude ? candidateA : candidateB;
    }

    // NOTE: reconstructed using Bounds.Encapsulate rather than transcribing the dense manual
    // min/max blending in the decompiled code - computes the gun's local-space bounding box by
    // transforming all 8 world-space corners of every child Renderer's bounds into local space.
    // Verified the corner bit-selection (bit0->x sign, bit1->y sign, corner>=4->+z else -z) and
    // the min/max blending against raw's manual arithmetic; both are exactly reproduced here.
    private bool TryLocalBounds(Transform gun, out Bounds localBounds)
    {
        localBounds = new Bounds(Vector3.zero, Vector3.zero);

        Renderer[] renderers = gun.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            return false;
        }

        Matrix4x4 worldToLocal = gun.worldToLocalMatrix;
        bool first = true;

        foreach (Renderer r in renderers)
        {
            Bounds worldBounds = r.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 offset = new Vector3(
                    (corner & 1) != 0 ? worldBounds.extents.x : -worldBounds.extents.x,
                    (corner & 2) != 0 ? worldBounds.extents.y : -worldBounds.extents.y,
                    corner < 4 ? -worldBounds.extents.z : worldBounds.extents.z);
                Vector3 worldCorner = worldBounds.center + offset;
                Vector3 localCorner = worldToLocal.MultiplyPoint3x4(worldCorner);

                if (first)
                {
                    localBounds = new Bounds(localCorner, Vector3.zero);
                    first = false;
                }
                else
                {
                    localBounds.Encapsulate(localCorner);
                }
            }
        }

        return true;
    }

    // FIX (confirmed): raw throws (jumps to the standard NRE-throw helper) when muzzlePoint is
    // null here, rather than returning a value. A prior draft silently returned Vector3.zero.
    // Currently unreachable in practice (UpdateBarrelCrosshair, the only call site, already
    // returns before calling this if muzzlePoint is null), but matched to raw for correctness.
    //
    // NOTE: structurally identical to CrosshairTarget above but raycasts from the muzzle along
    // ShotDir() instead of from the camera along its forward vector.
    private Vector3 BarrelAimPoint(out GameObject hitGo)
    {
        hitGo = null;
        if (muzzlePoint == null)
        {
            throw new NullReferenceException("muzzlePoint not available");
        }

        Vector3 origin = muzzlePoint.position;
        Vector3 dir = ShotDir().normalized;

        int hitCount = Physics.RaycastNonAlloc(new Ray(origin, dir), _aimHits, 1000f);

        int bestIndex = -1;
        float bestDist = float.MaxValue;

        for (int i = 0; i < hitCount; i++)
        {
            Collider col = _aimHits[i].collider;
            Transform hitT = col.transform;

            if (playerTf != null && (hitT == playerTf || hitT.IsChildOf(playerTf)))
            {
                continue;
            }

            if (!(col is CharacterController) && !Bullet.BlocksShot(col.gameObject))
            {
                continue;
            }

            float dist = _aimHits[i].distance;
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIndex = i;
            }
        }

        if (bestIndex >= 0)
        {
            hitGo = _aimHits[bestIndex].collider.gameObject;
            return _aimHits[bestIndex].point;
        }

        return origin + dir * 200f;
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

    private void BuildAimZone()
    {
        bool canvasCreated;
        GameObject go = SceneUI.GetOrCreateCanvas("Mode1AimCanvas", out canvasCreated);
        Canvas canvas = SceneUI.GetOrAdd<Canvas>(go);

        if (canvasCreated)
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 4;

            CanvasScaler scaler = SceneUI.GetOrAdd<CanvasScaler>(go);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 1f;
        }
        SceneUI.GetOrAdd<GraphicRaycaster>(go);

        Transform canvasT = go.transform;
        bool zoneCreated;
        GameObject zoneGo = SceneUI.GetOrCreate("AimZone", canvasT, out zoneCreated);
        Image zoneImg = SceneUI.GetOrAdd<Image>(zoneGo);
        zoneImg.color = new Color(0f, 0f, 0f, 0f);
        zoneImg.raycastTarget = true;

        RectTransform zoneRt = zoneImg.rectTransform;
        zoneRt.anchorMin = Vector2.zero;
        zoneRt.anchorMax = Vector2.one;
        zoneRt.offsetMin = Vector2.zero;
        zoneRt.offsetMax = Vector2.zero;

        // NOTE: SceneUI.GetOrAdd<UIDrag> - a custom drag-handling component, not decompiled in
        // this paste. Field name for the drag callback is a guess based on how it's written.
        UIDrag drag = SceneUI.GetOrAdd<UIDrag>(zoneGo);
        drag.cb = OnAimDrag;

        aimZone = zoneGo;
    }

    private void BuildFireButton()
    {
        bool canvasCreated;
        GameObject go = SceneUI.GetOrCreateCanvas("FireButtonCanvas", out canvasCreated);
        Canvas canvas = SceneUI.GetOrAdd<Canvas>(go);

        if (canvasCreated)
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 8;

            CanvasScaler scaler = SceneUI.GetOrAdd<CanvasScaler>(go);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            // Confirmed via IEEE-754 decode: (1080, 1920) - portrait - unlike the (1920, 1080)
            // landscape reference used by MatchHUD/MatchmakingUI's canvases in earlier sessions.
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 1f;
        }
        SceneUI.GetOrAdd<GraphicRaycaster>(go);

        BuildAimZone();

        Transform canvasT = go.transform;
        bool fireBtnCreated;
        GameObject fireBtnGo = SceneUI.GetOrCreate("FireBtn", canvasT, out fireBtnCreated);
        Image fireBg = SceneUI.GetOrAdd<Image>(fireBtnGo);

        if (fireBgSprite != null)
        {
            fireBg.sprite = fireBgSprite;
            fireBg.color = Color.white;
            fireBg.preserveAspect = true;

            if (fireBtnCreated)
            {
                RectTransform rt = fireBg.rectTransform;
                rt.anchorMax = new Vector2(1f, 0.5f);
                rt.anchorMin = new Vector2(1f, 0.5f);
                rt.pivot = new Vector2(1f, 0.5f);
                rt.anchoredPosition = new Vector2(-110f, 20f);
                rt.sizeDelta = new Vector2(180f, 180f);
            }
        }
        else
        {
            fireBg.sprite = null;
            fireBg.color = new Color(0.85f, 0.25f, 0.2f, 0.95f);

            if (fireBtnCreated)
            {
                RectTransform rt = fireBg.rectTransform;
                rt.anchorMax = new Vector2(1f, 0f);
                rt.anchorMin = new Vector2(1f, 0f);
                rt.pivot = new Vector2(1f, 0f);
                rt.anchoredPosition = new Vector2(-60f, 70f);
                rt.sizeDelta = new Vector2(190f, 190f);
            }
        }

        Button fireButton = SceneUI.GetOrAdd<Button>(fireBtnGo);
        fireButton.targetGraphic = fireBg;

        if (gunIcon != null)
        {
            bool iconCreated;
            GameObject iconGo = SceneUI.GetOrCreate("GunIcon", fireBtnGo.transform, out iconCreated);
            Image icon = SceneUI.GetOrAdd<Image>(iconGo);
            icon.sprite = gunIcon;
            icon.color = Color.white;
            icon.raycastTarget = false;
            icon.preserveAspect = true;

            RectTransform iconRt = icon.rectTransform;
            iconRt.anchorMax = new Vector2(0.5f, 0.5f);
            iconRt.anchorMin = new Vector2(0.5f, 0.5f);
            iconRt.pivot = new Vector2(0.5f, 0.5f);
            iconRt.anchoredPosition = Vector2.zero;
            iconRt.sizeDelta = new Vector2(120f, 100f);
        }

        // NOTE: this "L"-named fallback label is always ensured (GetOrCreate is idempotent)
        // regardless of whether an icon sprite was used above - it's simply left inactive when
        // the icon is shown. Confirmed from the decompiled control flow: the icon branch has no
        // early-out that would skip this.
        bool labelCreated;
        GameObject labelGo = SceneUI.GetOrCreate("L", fireBtnGo.transform, out labelCreated);
        Text fireLabel = SceneUI.GetOrAdd<Text>(labelGo);
        fireLabel.font = font;
        fireLabel.raycastTarget = false;

        if (labelCreated)
        {
            fireLabel.text = "FIRE";
            fireLabel.fontSize = 46;
            fireLabel.alignment = TextAnchor.MiddleCenter;
            fireLabel.color = Color.white;
            fireLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
            fireLabel.verticalOverflow = VerticalWrapMode.Overflow;

            RectTransform labelRt = fireLabel.rectTransform;
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = Vector2.zero;
            labelRt.offsetMax = Vector2.zero;
        }

        fireLabel.gameObject.SetActive(gunIcon == null);
        fireBtn = fireBtnGo;

        Transform jumpT = canvasT.Find("Jump");
        jumpBtn = (jumpT != null) ? jumpT.gameObject : null;

        Transform namT = canvasT.Find("btnnam");
        namBtn = (namT != null) ? namT.gameObject : null;
        _namBtnImg = (namBtn != null) ? namBtn.GetComponent<Image>() : null;

        // NOTE: decompiled reads this via a field offset mislabeled "m_Corners" (a Graphic
        // vertex-corner array, not a Sprite reference) - reconstructed as lazily backfilling
        // namOffSprite from the nam button's current sprite if it wasn't already assigned.
        if (namOffSprite == null && _namBtnImg != null)
        {
            namOffSprite = _namBtnImg.sprite;
        }

        BuildCrosshair(canvasT);
    }

    private void BuildCrosshair(Transform canvas)
    {
        bool created;
        GameObject go = SceneUI.GetOrCreate("Crosshair", canvas, out created);
        RectTransform rt = SceneUI.GetOrAdd<RectTransform>(go);

        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(64f, 64f);

        Transform crosshairT = go.transform;
        Color barColor = new Color(1f, 1f, 1f, 0.85f);
        CrosshairBar(crosshairT, "Up", new Vector2(0f, 18f), new Vector2(4f, 18f), barColor);
        CrosshairBar(crosshairT, "Down", new Vector2(0f, -18f), new Vector2(4f, 18f), barColor);
        CrosshairBar(crosshairT, "Left", new Vector2(-18f, 0f), new Vector2(18f, 4f), barColor);
        CrosshairBar(crosshairT, "Right", new Vector2(18f, 0f), new Vector2(18f, 4f), barColor);
        CrosshairBar(crosshairT, "Dot", Vector2.zero, new Vector2(6f, 6f), barColor);

        crosshair = go;
    }

    // Builds one crosshair bar as a dim background "glow" plate plus a smaller foreground bar
    // (named "F") inset by 1.5 units on each side, in the given color.
    private void CrosshairBar(Transform parent, string name, Vector2 pos, Vector2 size, Color col)
    {
        bool created;
        GameObject go = SceneUI.GetOrCreate(name, parent, out created);
        Image glow = SceneUI.GetOrAdd<Image>(go);
        glow.color = new Color(0f, 0f, 0f, 0.65f);
        glow.raycastTarget = false;

        RectTransform rt = glow.rectTransform;
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = new Vector2(size.x + 3f, size.y + 3f);

        bool fgCreated;
        GameObject fgGo = SceneUI.GetOrCreate("F", go.transform, out fgCreated);
        Image fg = SceneUI.GetOrAdd<Image>(fgGo);
        fg.color = col;
        fg.raycastTarget = false;

        RectTransform fgRt = fg.rectTransform;
        fgRt.anchorMin = Vector2.zero;
        fgRt.anchorMax = Vector2.one;
        fgRt.offsetMin = new Vector2(1.5f, 1.5f);
        fgRt.offsetMax = new Vector2(-1.5f, -1.5f);
    }

    private void OnAimDrag(PointerEventData e)
    {
        if (gmm != null && gmm.SeekerHideCountdown)
        {
            return;
        }

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
            camCtrl.aimPitchDeg = aimPitch;
        }
    }
}