using Meccha.Client;
using Meccha.Shared.Game;
using UnityEngine;

// Plays another player's body from what the server replicates: lying down and climbing from the
// replicated flags, moving from how the body is being drawn, and a shot when one is drawn. Their
// struck pose comes from OnlineRound.ApplyRemoteLoadout. BodyAnimation does the switching, exactly
// as it does for the player's own body and the bots. A bot this game hosts is left to its controller.
//
// On a wall the body faces the wall, as the bots' do. The facing the server sends is where the player
// is aiming, which on a seeker can be anywhere, so the wall is found here, in this game's own room.
[DisallowMultipleComponent]
public class NetBodyAnimator : MonoBehaviour
{
    private const float WallTurnSeconds = 0.15f;
    private const float WallLookInterval = 0.2f;
    private const float WallLookDistance = 6f;          // WallClimber's reach, and the body drawn a little late
    private const float WallStayDistance = 8f;          // how far off the wall being climbed may be and still be it
    private const float WallSwingDegreesPerSecond = 540f;
    private const float MinWallSteepness = 50f;         // WallClimber.minWallSteepness
    private const int WallLookDirections = 8;
    private static readonly float[] WallLookHeights = { 0.15f, 0.45f, 0.7f, 0.92f };
    private static readonly RaycastHit[] Hits = new RaycastHit[16];

    private NetEntityView view;
    private Animator anim;
    private float wallTurn;             // 0 = as the server says, 1 = facing the wall
    private float nextWallLookAt;
    private bool wallFound;
    private Quaternion facingWall, shownFacing;

    public void Bind(NetEntityView entity)
    {
        view = entity;
        view.DrivesAnimatorSpeed = false;
        anim = GetComponentInChildren<Animator>();
    }

    /// <summary>The shot's gun animation (the sound and tracer are OnlineRound's).</summary>
    public void PlayShot()
    {
        if (view != null && !view.Eliminated) BodyAnimation.PlayShot(anim);
    }

    private void LateUpdate()
    {
        if (view == null || anim == null || view.Eliminated || view.IsHosted) return;

        EntityFlags flags = view.Flags;
        BodyAnimation.SetProne(anim, (flags & EntityFlags.Prone) != 0);
        BodyAnimation.SetClimbing(anim, (flags & EntityFlags.Climbing) != 0);
        BodyAnimation.SetSpeed(anim, view.AnimationSpeed / GameTuning.MoveSpeed);
        FaceWall((flags & EntityFlags.Climbing) != 0);
    }

    // After NetEntityView has placed the body for this frame.
    private void FaceWall(bool climbing)
    {
        if (!climbing) wallFound = false;
        else if (Time.unscaledTime >= nextWallLookAt)
        {
            nextWallLookAt = Time.unscaledTime + WallLookInterval;
            LookForWall();
        }

        wallTurn = Mathf.MoveTowards(wallTurn, wallFound ? 1f : 0f, Time.unscaledDeltaTime / WallTurnSeconds);
        if (wallTurn <= 0f) return;

        shownFacing = Quaternion.RotateTowards(shownFacing, facingWall, WallSwingDegreesPerSecond * Time.unscaledDeltaTime);
        transform.rotation = Quaternion.Slerp(transform.rotation, shownFacing, wallTurn);
    }

    private static bool IsClimbable(in RaycastHit hit) =>
        !Bullet.IsCharacter(hit.collider) && !hit.collider.CompareTag("Ground") &&
        Vector3.Angle(hit.normal, Vector3.up) >= MinWallSteepness;

    // The nearest wall around the body, by WallClimber's rule of what can be climbed.
    private void LookForWall()
    {
        Vector3 feet = view.FeetPosition;
        float height = GameTuning.PlayerHeight * transform.lossyScale.y;

        // The wall being climbed, while it is still there: a body going up past shelves and frames
        // must not swing round to each of them in turn.
        if (wallFound)
        {
            foreach (float up in WallLookHeights)
            {
                int ahead = Physics.RaycastNonAlloc(feet + Vector3.up * (height * up), facingWall * Vector3.forward, Hits,
                                                    WallStayDistance, ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < ahead; i++)
                    if (IsClimbable(Hits[i])) return;
            }
        }

        float nearest = float.MaxValue;
        Vector3 normal = Vector3.zero;

        foreach (float up in WallLookHeights)
        {
            Vector3 from = feet + Vector3.up * (height * up);

            for (int d = 0; d < WallLookDirections; d++)
            {
                Vector3 direction = Quaternion.Euler(0f, d * 360f / WallLookDirections, 0f) * Vector3.forward;
                int count = Physics.RaycastNonAlloc(from, direction, Hits, WallLookDistance, ~0, QueryTriggerInteraction.Ignore);

                for (int i = 0; i < count; i++)
                {
                    if (!IsClimbable(Hits[i]) || Hits[i].distance >= nearest) continue;

                    nearest = Hits[i].distance;
                    normal = Hits[i].normal;
                }
            }
        }

        normal.y = 0f;
        if (nearest == float.MaxValue || normal.sqrMagnitude < 0.0001f) return;   // keeps the last wall

        facingWall = Quaternion.LookRotation(-normal.normalized, Vector3.up);
        if (!wallFound) shownFacing = facingWall;       // the first wall of a climb: blended in from how they stand
        wallFound = true;
    }
}
