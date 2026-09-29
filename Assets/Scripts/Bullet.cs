using System;
using UnityEngine;

public class Bullet : MonoBehaviour
{
    [Header("Tunables")]
    public float speed = 160f;
    public float maxLife = 3f;
    public float radius = 0.6f;

    // When true, the bullet ignores non-character obstacles that BlocksShot() says shouldn't stop it
    // (e.g. small props), only stopping at characters or at "structural" geometry (walls/doors/etc).
    public bool passThroughProps;

    private float life;
    private Transform owner;
    private Action<GameObject> onHitCb;
    private BotHideController hide;

    private readonly RaycastHit[] _hits = new RaycastHit[32];

    // Used by BotHideController's bots (routes hits through BotHideController.TryShoot).
    public void Init(BotHideController h, Transform shooter)
    {
        hide = h;
        owner = shooter;
    }

    // Used by BotSeekController's bots (routes hits through an explicit callback instead).
    public void InitShot(Transform shooter, Action<GameObject> onHit)
    {
        owner = shooter;
        onHitCb = onHit;
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        float moveDist = speed * dt;

        Transform tf = transform;
        Vector3 origin = tf.position;
        Vector3 forward = tf.forward;

        // NOTE: layer mask in the decompiled call is the raw int -5 (0xFFFFFFFB), i.e. every layer
        // except layer 2 — kept exact rather than simplified to "everything", in case layer 2 is
        // deliberately excluded (e.g. an "Ignore Bullets" layer).
        int mask = ~(1 << 2);
        // CORRECTION: the decompiled literal here is 1, which is QueryTriggerInteraction.Ignore
        // (UseGlobal=0, Ignore=1, Collide=2) — same mapping already confirmed elsewhere in this
        // codebase (e.g. WallClimber.SampleSurface). The earlier draft used .Collide instead.
        int count = Physics.SphereCastNonAlloc(origin, radius, forward, _hits, moveDist, mask, QueryTriggerInteraction.Ignore);

        int bestIdx = -1;
        float bestDist = float.MaxValue;

        for (int i = 0; i < count; i++)
        {
            Collider col = _hits[i].collider;

            bool ignore = false;
            if (owner != null)
            {
                if (col != null && col.gameObject == owner.gameObject)
                {
                    ignore = true;
                }
                else if (col != null && col.transform.IsChildOf(owner))
                {
                    ignore = true;
                }
            }

            if (ignore) continue;

            if (passThroughProps && !IsCharacter(col) && col != null && !BlocksShot(col.gameObject))
            {
                continue; // pass through non-blocking scenery/props
            }

            float dist = _hits[i].distance;
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx = i;
            }
        }

        if (bestIdx >= 0)
        {
            tf.position = _hits[bestIdx].point;
            Collider hitCol = _hits[bestIdx].collider;
            if (hitCol != null)
            {
                OnHit(hitCol.gameObject);
            }
            return;
        }

        tf.position = origin + forward * moveDist;

        life += dt;
        if (life >= maxLife)
        {
            Destroy(gameObject);
        }
    }

    private void OnHit(GameObject target)
    {
        if (target == null) return;

        Debug.Log("[Bullet] Trúng mục tiêu: " + target.name);

        if (onHitCb != null)
        {
            onHitCb(target);
        }
        else if (hide != null)
        {
            hide.TryShoot(target);
        }

        Destroy(gameObject);
    }

    // True for character-rig colliders (CharacterController, or anything with an Animator in its
    // parent hierarchy) — used to distinguish "someone I could actually catch/hit" from scenery.
    public static bool IsCharacter(Collider col)
    {
        if (col is CharacterController) return true;
        if (col == null) return false;

        return col.GetComponentInParent<Animator>() != null;
    }

    // True for "structural" room geometry that should stop a bullet: the room shell itself, windows,
    // doors, wallpaper, window panes, walls, and roofs. Floors never block a shot, even if their name
    // would otherwise match one of the other categories.
    public static bool BlocksShot(GameObject go)
    {
        if (go == null) return false;

        string name = go.name;
        if (name.Contains("Floor")) return false;

        int underscoreIdx = name.IndexOf('_');
        string prefix = underscoreIdx < 1 ? name : name.Substring(0, underscoreIdx);

        if (prefix.StartsWith("Room")) return true;

        // CORRECTION: this checks the PREFIX (name up to the first underscore), not the full name
        // — confirmed via trace, the decompile passes the same prefix variable used for the
        // StartsWith checks above, not the full name like Contains(Wallpaper/WindowPane/Walls/Roof)
        // below use. The earlier draft used `name.EndsWith("Door")`, which is a different check
        // (e.g. it would match "Something_Door" where the prefix check would not).
        if (!prefix.StartsWith("Window") &&
            !prefix.EndsWith("Door") &&
            !name.Contains("Wallpaper") &&
            !name.Contains("WindowPane") &&
            !name.Contains("Walls"))
        {
            return name.Contains("Roof");
        }

        return true;
    }
}