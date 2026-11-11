using Meccha.Shared.Game;
using UnityEngine;

// A bullet drawn in an online round: this player's own, or another's as the server relays it. The
// server flies the real one and says who it reached (OnEliminated); this is the one on screen.
// Other players' bodies have no colliders here, so left to itself it flew on through a hider and
// into the wall behind. It stops on the body it reaches, by the test the server flies its own
// bullet with, at the speed the server flies it. Who is out stays the server's word.
[DisallowMultipleComponent]
public sealed class NetBullet : MonoBehaviour
{
    private Vector3 muzzle, direction;
    private float pad, flown;

    /// <param name="flownAlready">How far from the muzzle it starts: a shot heard of late is already on its way.</param>
    public static void Attach(Bullet bullet, Vector3 muzzle, Vector3 direction, float sizeScale, float flownAlready = 0f)
    {
        if (bullet == null) return;

        bullet.speed = GameTuning.BulletSpeed;
        bullet.maxLife = Mathf.Max(0.05f, GameTuning.BulletLifeSeconds - flownAlready / GameTuning.BulletSpeed);

        NetBullet net = bullet.gameObject.AddComponent<NetBullet>();
        net.muzzle = muzzle;
        net.direction = direction.normalized;
        net.pad = GameTuning.BulletRadius * sizeScale;
        net.flown = flownAlready;
    }

    // After Bullet.Update has moved it for this frame.
    private void LateUpdate()
    {
        float now = Vector3.Dot(transform.position - muzzle, direction);
        if (now <= flown) return;

        if (OnlineRound.HiderBetween(muzzle, direction, flown, now, pad, out float at))
        {
            transform.position = muzzle + direction * at;
            Destroy(gameObject);
            return;
        }

        flown = now;
    }
}
