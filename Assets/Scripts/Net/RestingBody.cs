using Meccha.Client;
using UnityEngine;

/// <summary>
/// The body of a player who is out, online (2026-11-26). Its ragdoll falls; once it lies still it
/// is frozen where it lies (no more sliding, sinking or vanishing), and the game that ran the fall
/// (the player's own, or the bot host's for a bot) tells the server where (protocol 16,
/// BodyRestCodec). Every other game moves its body there when the server passes that on, so all of
/// them show the body in the same place. A body laid down after the fact (a rejoin, an elimination
/// this game never saw) settles out of sight and is drawn only once it lies at that place: nobody
/// sees it fall a second time.
/// </summary>
[DisallowMultipleComponent]
public sealed class RestingBody : MonoBehaviour
{
    private const float LeastFall = 0.6f;     // seconds before a body can count as lying still
    private const float LongestFall = 4f;     // and at the latest
    private const float GlideSeconds = 0.35f;

    private byte slot;
    private bool reports;
    private bool hidden;
    private Ragdoll ragdoll;
    private float since;
    private bool settled;
    private bool haveTarget;
    private Vector3 target;
    private float glideAt = -1f;
    private Vector3 glideFrom, glideTo;

    /// <summary>Whether the body lies still and frozen.</summary>
    public bool Settled => settled;

    /// <param name="reports">This game ran the fall: it says where the body came to rest.</param>
    /// <param name="hidden">Laid down after the fact: drawn only once it lies at its place.</param>
    public static RestingBody Attach(Transform body, byte slot, Ragdoll ragdoll, bool reports, bool hidden)
    {
        if (body == null) return null;
        RestingBody rest = body.GetComponent<RestingBody>() ?? body.gameObject.AddComponent<RestingBody>();
        rest.slot = slot;
        rest.ragdoll = ragdoll;
        rest.reports = reports;
        rest.since = Time.time;
        rest.settled = false;
        rest.glideAt = -1f;
        if (hidden) rest.Hide(true);

        MecchaNetClient net = MecchaNetClient.Instance;
        if (net != null && net.TryBodyRest(slot, out Vector3 at)) rest.RestAt(at);
        return rest;
    }

    /// <summary>Where the server says this body lies.</summary>
    public void RestAt(Vector3 at)
    {
        haveTarget = true;
        target = at;
        if (settled) MoveTo(at, glide: !hidden);
    }

    private void Update()
    {
        if (!settled)
        {
            float fallen = Time.time - since;
            bool still = ragdoll == null || !ragdoll.IsRagdolling || ragdoll.Resting;
            if (fallen < LeastFall || (!still && fallen < LongestFall)) return;
            Settle();
            return;
        }

        if (glideAt < 0f) return;
        float t = Mathf.Clamp01((Time.time - glideAt) / GlideSeconds);
        transform.position = Vector3.Lerp(glideFrom, glideTo, t * t * (3f - 2f * t));
        if (t >= 1f) glideAt = -1f;
    }

    private void Settle()
    {
        settled = true;
        if (ragdoll != null) ragdoll.Freeze();

        if (haveTarget) MoveTo(target, glide: false);   // the place the server has: no fall seen here
        else if (reports)
        {
            MecchaNetClient net = MecchaNetClient.Instance;
            if (net != null) net.ReportBodyRest(slot, OnlineRound.DrawnCentre(transform));
        }

        if (hidden) Hide(false);
    }

    // The whole body, frozen, carried so its middle as drawn is at that place.
    private void MoveTo(Vector3 at, bool glide)
    {
        Vector3 delta = at - OnlineRound.DrawnCentre(transform);
        if (delta.sqrMagnitude < 0.0004f) return;
        if (!glide)
        {
            transform.position += delta;
            glideAt = -1f;
            return;
        }
        glideFrom = transform.position;
        glideTo = glideFrom + delta;
        glideAt = Time.time;
    }

    private void Hide(bool off)
    {
        hidden = off;
        foreach (Renderer r in GetComponentsInChildren<Renderer>(true)) r.forceRenderingOff = off;
    }

    private void OnDestroy()
    {
        if (hidden) Hide(false);
    }
}
