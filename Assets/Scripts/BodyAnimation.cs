using UnityEngine;

// The one place a character's Animator is switched: the player's, the bots' and other players'
// online. A script says what is true of the body (moving, on a wall, lying down, in a struck pose,
// carrying a gun) and this decides what plays, by priority, telling the Animator only when that
// changes:
//
//     lying down > struck pose > climbing > standing or running
//
// What it prevents, all of which replayed an animation from its first frame:
//   * "Pose" and "Nam" are both entered from Any State, so with both on the body hopped from one
//     to the other. Only one is ever on here.
//   * Idle and run start over each time they are entered, so a single frame of stillness (a bot
//     re-planning, a late snapshot, a missed wall ray) restarted the run. Moving and climbing come
//     on at once but go off only after staying off for a moment.
//   * A shot asked for while the last one is playing was held by the Animator and played again
//     afterwards. Now the one playing finishes and the extra one is dropped.
//
// The "Gun" layer overrides the base layer, so on a body seen from outside it fades out while a
// base-layer pose is on and back when the pose ends. The body a seeker looks out of keeps it: the
// arms and the gun are what they see and aim with, and a pose would swing them round the camera.
[DisallowMultipleComponent]
public sealed class BodyAnimation : MonoBehaviour
{
    private const float MovingAtSpeed = 0.1f;       // the controller's own idle/run threshold
    private const float OffDelaySeconds = 0.12f;    // as long as the idle/run blend itself

    /// <summary>How long the player's own body keeps running after the stick is let go.</summary>
    public const float PlayerStopSeconds = OffDelaySeconds;

    // Bots pause for a beat between legs, and other players arrive through the network: that long
    // standing still is not a stop worth starting the run over for.
    private const float OtherBodyStopSeconds = 0.3f;
    private const float GunFadeSeconds = 0.15f;
    private const float ShotFinishedAt = 0.9f;      // where the controller's shot state leaves for idle
    private const int ClimbingPose = 1;             // the clip the controller uses for a wall

    private static readonly int SpeedHash = Animator.StringToHash("Speed");
    private static readonly int PoseHash = Animator.StringToHash("Pose");
    private static readonly int NamHash = Animator.StringToHash("Nam");
    private static readonly int GunFireHash = Animator.StringToHash("GunFire");
    private static readonly int GunAttackState = Animator.StringToHash("GunAtk");

    // On at once; off only once it has stayed off for OffDelaySeconds.
    private struct HeldState
    {
        public bool On;
        private float offAt;

        public void Request(bool on, float now, float offDelay)
        {
            if (on) { On = true; offAt = -1f; }
            else if (On && offAt < 0f) offAt = now + offDelay;
        }

        public void Tick(float now)
        {
            if (On && offAt >= 0f && now >= offAt) Clear();
        }

        public void Clear()
        {
            On = false;
            offAt = -1f;
        }
    }

    private Animator anim;
    private bool bound, hasSpeed, hasPose, hasNam, hasShot;
    private int gunLayer = -1;

    private HeldState moving, climbing;
    private bool prone, carriesGun, firstPerson;
    private int struckPose;

    // ================= What scripts say about the body =================

    /// <summary>How fast the body moves, 0 to 1 of full speed.</summary>
    public static void SetSpeed(Animator animator, float speed, float stopAfter = OtherBodyStopSeconds)
    {
        BodyAnimation body = Of(animator);
        if (body == null) return;
        body.moving.Request(speed >= MovingAtSpeed, Time.unscaledTime, stopAfter);
        body.Apply();
    }

    /// <summary>The Animator "Pose" value of a struck pose; 0 for none.</summary>
    public static void SetPose(Animator animator, int pose)
    {
        BodyAnimation body = Of(animator);
        if (body == null) return;
        body.struckPose = pose;
        body.Apply();
    }

    public static void SetClimbing(Animator animator, bool on)
    {
        BodyAnimation body = Of(animator);
        if (body == null) return;
        body.climbing.Request(on, Time.unscaledTime, OffDelaySeconds);
        body.Apply();
    }

    public static void SetProne(Animator animator, bool on)
    {
        BodyAnimation body = Of(animator);
        if (body == null) return;
        body.prone = on;
        body.Apply();
    }

    /// <summary>Whether the body holds a gun: a seeker's "Gun" layer. firstPerson: the player looks out of this body.</summary>
    public static void SetCarriesGun(Animator animator, bool on, bool firstPerson = false)
    {
        BodyAnimation body = Of(animator);
        if (body == null) return;
        body.carriesGun = on;
        body.firstPerson = firstPerson;
    }

    /// <summary>The gun's shot animation, unless one is still playing.</summary>
    public static void PlayShot(Animator animator)
    {
        BodyAnimation body = Of(animator);
        if (body == null || !body.Ready || !body.hasShot) return;

        if (body.gunLayer >= 0)
        {
            AnimatorStateInfo playing = animator.GetCurrentAnimatorStateInfo(body.gunLayer);
            if (playing.shortNameHash == GunAttackState && playing.normalizedTime < ShotFinishedAt) return;
        }

        animator.SetTrigger(GunFireHash);
    }

    private static BodyAnimation Of(Animator animator)
    {
        if (animator == null) return null;
        if (!animator.TryGetComponent(out BodyAnimation body)) body = animator.gameObject.AddComponent<BodyAnimation>();
        body.anim = animator;
        return body;
    }

    // ================= What the Animator is told =================

    // Not until the Animator can take it: a body that is switched off is brought up to date when it
    // comes back on.
    private bool Ready
    {
        get
        {
            if (anim == null || !anim.isActiveAndEnabled || anim.runtimeAnimatorController == null) return false;
            if (bound) return true;

            bound = true;
            foreach (AnimatorControllerParameter p in anim.parameters)
            {
                if (p.nameHash == SpeedHash) hasSpeed = true;
                else if (p.nameHash == PoseHash) hasPose = true;
                else if (p.nameHash == NamHash) hasNam = true;
                else if (p.nameHash == GunFireHash) hasShot = true;
            }

            for (int i = 0; i < anim.layerCount; i++)
            {
                if (anim.GetLayerName(i) == "Gun") gunLayer = i;
            }

            return true;
        }
    }

    private void Apply()
    {
        if (!Ready) return;

        int pose = prone ? 0 : struckPose != 0 ? struckPose : climbing.On ? ClimbingPose : 0;

        if (hasNam && anim.GetBool(NamHash) != prone) anim.SetBool(NamHash, prone);
        if (hasPose && anim.GetInteger(PoseHash) != pose) anim.SetInteger(PoseHash, pose);

        float speed = moving.On ? 1f : 0f;
        if (hasSpeed && !Mathf.Approximately(anim.GetFloat(SpeedHash), speed)) anim.SetFloat(SpeedHash, speed);
    }

    private void LateUpdate()
    {
        float now = Time.unscaledTime;
        moving.Tick(now);
        climbing.Tick(now);

        Apply();
        if (Ready) FadeGunLayer();
    }

    private void FadeGunLayer()
    {
        if (gunLayer < 0) return;

        bool gunUp = carriesGun && !prone && (firstPerson || (struckPose == 0 && !climbing.On));
        float target = gunUp ? 1f : 0f;
        float weight = anim.GetLayerWeight(gunLayer);
        if (!Mathf.Approximately(weight, target))
            anim.SetLayerWeight(gunLayer, Mathf.MoveTowards(weight, target, Time.unscaledDeltaTime / GunFadeSeconds));
    }

    // The Animator forgets everything when its object is switched off; so does this.
    private void OnDisable()
    {
        moving.Clear();
        climbing.Clear();
        prone = false;
        carriesGun = false;
        firstPerson = false;
        struckPose = 0;
    }
}
