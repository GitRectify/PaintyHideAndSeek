using System.Collections.Generic;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;

/// <summary>What Single Play's seeker controller needs to know to run bots in an online match.</summary>
public interface IOnlineBotGame
{
    /// <summary>A real player, with paint to judge and the spot sequence to go through.</summary>
    bool IsHuman(Transform target);

    Color ColorOf(Transform human);
    Bounds BoundsOf(Transform human);
    void BotFire(Transform bot, Vector3 origin, Vector3 direction);

    /// <summary>Where a stranded seeker goes as a last resort: a real spawn, whoever placed it last.</summary>
    Vector3 RescuePoint(Transform bot, int index);

    void BotSpot(Transform bot, Transform human);
}

// Part of OnlineRound (OnlineRound.cs has the overview). Client-hosted bots: the server has no map,
// so when it gives this player the bots (OnBotHost) this game runs them with Single Play's own
// controllers on the bodies of the empty seats, and tells the server what they do. The server still
// judges every shot; other players see the bots as they see anyone else.
//
// Hider bots go straight to a hiding place once (BotHideController), then stay put, so a host taking
// over mid-round is only given the seeker bots, and carries on from where they are.
public partial class OnlineRound : IOnlineBotGame
{
    private sealed class HostedBot
    {
        public byte Slot;
        public Role Role;
        public byte SpawnIndex;
        public NetEntityView View;
        public bool InRoom;             // this client has the body where it should be
        public BotState Sent;
        public bool EverSent;
        public float SentAt;
        public Renderer Body;
        public Animator Animator;
    }

    private const float BotKeepAliveSeconds = 0.5f;
    private const float BotTargetRefreshSeconds = 0.5f;

    private readonly Dictionary<byte, HostedBot> hostedBots = new Dictionary<byte, HostedBot>();
    private readonly Dictionary<Transform, byte> slotOfBody = new Dictionary<Transform, byte>();
    private readonly HashSet<Transform> humanHiders = new HashSet<Transform>();
    private readonly List<Transform> botTargets = new List<Transform>();
    private BotState[] botStates = new BotState[BotCodec.MaxBotsPerReport];

    private BotHideController hideBots;
    private BotSeekController seekBots;
    private bool hidingBots, seekingBots;
    private int botRoomPreparedFor = -1;     // the match whose navigation has been baked
    private float nextBotReportAt, botKeepAliveAt, nextBotTargetsAt;

    /// <summary>The bots this game is running now, for diagnostics.</summary>
    public static int HostedBotCount => Active ? _instance.hostedBots.Count : 0;

    // The server's word on who runs the bots: the whole list each time, or none.
    private void HandleBotHost(BotAssignment[] bots)
    {
        StopHostingBots();
        if (bots == null || bots.Length == 0) return;

        hideBots = FindAnyObjectByType<BotHideController>(FindObjectsInactive.Include);
        seekBots = FindAnyObjectByType<BotSeekController>(FindObjectsInactive.Include);

        foreach (BotAssignment bot in bots)
            hostedBots[bot.Slot] = new HostedBot { Slot = bot.Slot, Role = RoleOf(bot.Slot), SpawnIndex = bot.SpawnIndex };

        Debug.Log($"[Online] Hosting {bots.Length} bot(s)");
    }

    private void StopHostingBots()
    {
        if (hideBots != null) hideBots.EndOnline();
        if (seekBots != null) seekBots.EndOnline();

        foreach (HostedBot bot in hostedBots.Values)
        {
            if (bot.View == null) continue;
            bot.View.SetHosted(false);
            bot.View.SetShown(true);
        }

        hostedBots.Clear();
        slotOfBody.Clear();
        humanHiders.Clear();
        hidingBots = seekingBots = false;
    }

    private Role RoleOf(byte slot)
    {
        if (roster != null)
        {
            foreach (MecchaNetClient.RosterEntry e in roster)
                if (e.Slot == slot) return e.Role;
        }
        return Role.Fugitive;
    }

    private void PumpBots()
    {
        if (hostedBots.Count == 0) return;

        PrepareBotRoom();
        if (Net.Phase != MatchPhase.Hide && Net.Phase != MatchPhase.Seek) return;

        BringBotsIntoRoom();
        StartBotControllers();
        RefreshBotTargets();
        ReportBots();
    }

    // Baking navigation stalls the frame, so it is done once, as soon as this game hears it hosts
    // the bots. (A host who takes over mid-round takes the stall then.)
    private void PrepareBotRoom()
    {
        if (botRoomPreparedFor == matchId) return;
        botRoomPreparedFor = matchId;

        LevelManager level = FindAnyObjectByType<LevelManager>();
        string room = level != null ? level.CurrentRoomName : "Room";
        if (hideBots != null) hideBots.roomRootName = room;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (seekBots != null) seekBots.PrepareOnlineRoom(room);
        Debug.Log($"[Online] Room prepared for the bots in {clock.ElapsedMilliseconds} ms");
    }

    // Spawning ones go on their marker. One already in the round is taken over where it stands, as
    // soon as a snapshot has said where that is.
    private void BringBotsIntoRoom()
    {
        foreach (HostedBot bot in hostedBots.Values)
        {
            if (bot.InRoom) continue;

            bool spawning = bot.SpawnIndex != Snapshot.NoSpawn;
            bot.View = spawning ? NetEntityRegistry.Ensure(bot.Slot, bot.Role) : NetEntityRegistry.Get(bot.Slot);
            if (bot.View == null || (!spawning && !bot.View.HasPosition)) continue;

            Vector3? spot = spawning ? ResolveSpawn(bot.SpawnIndex, bot.Role == Role.Hunter) : (Vector3?)null;
            if (spawning && spot == null) continue;     // the room is not up yet

            bot.View.SetHosted(true);
            if (spot != null) bot.View.PlaceFeet(spot.Value);

            // A seeker holding the bots must not watch the hiders being placed.
            if (hunter && bot.Role == Role.Fugitive && Net.Phase == MatchPhase.Hide) bot.View.SetShown(false);

            bot.Body = bot.View.GetComponentInChildren<SkinnedMeshRenderer>(true);
            bot.Animator = bot.View.GetComponentInChildren<Animator>(true);
            slotOfBody[bot.View.transform] = bot.Slot;
            bot.InRoom = true;
        }
    }

    private void StartBotControllers()
    {
        foreach (HostedBot bot in hostedBots.Values)
            if (!bot.InRoom && bot.SpawnIndex != Snapshot.NoSpawn) return;

        if (!hidingBots && hideBots != null)
        {
            hidingBots = true;
            var names = new List<string>();
            foreach (HostedBot bot in hostedBots.Values)
                if (bot.Role == Role.Fugitive && bot.SpawnIndex != Snapshot.NoSpawn && bot.InRoom) names.Add(bot.View.name);
            if (names.Count > 0) hideBots.HideOnline(names.ToArray());
        }

        if (seekingBots || seekBots == null || Net.Phase != MatchPhase.Seek) return;

        // Taken over mid-round: during the seek the server sends the host everyone, so they all come.
        foreach (HostedBot bot in hostedBots.Values)
            if (!bot.InRoom) return;

        seekingBots = true;

        var seekers = new List<string>();
        foreach (HostedBot bot in hostedBots.Values)
        {
            bot.View.SetShown(true);
            if (bot.Role == Role.Hunter) seekers.Add(bot.View.name);
        }

        seekBots.BeginOnline(this, seekers.ToArray(), Net.PhaseSecondsLeft);
    }

    // Everyone still hiding, as this client knows them: bodies come and go with the snapshots.
    private void RefreshBotTargets()
    {
        if (!seekingBots || Time.unscaledTime < nextBotTargetsAt) return;
        nextBotTargetsAt = Time.unscaledTime + BotTargetRefreshSeconds;

        botTargets.Clear();
        humanHiders.Clear();

        foreach (MecchaNetClient.RosterEntry e in roster)
        {
            if (e.Role != Role.Fugitive || eliminated.Contains(e.Slot)) continue;

            bool mine = e.Slot == Net.LocalSlot;
            NetEntityView view = NetEntityRegistry.Get(e.Slot);
            Transform body = mine ? localBody : view != null && !view.Eliminated && (view.HasPosition || view.IsHosted) ? view.transform : null;
            if (body == null) continue;

            botTargets.Add(body);
            slotOfBody[body] = e.Slot;
            if (!e.IsBot) humanHiders.Add(body);
        }

        seekBots.SetTargets(botTargets);
    }

    private void ReportBots()
    {
        float now = Time.unscaledTime;
        if (now < nextBotReportAt) return;
        nextBotReportAt = now + 1f / NetTuning.InputSendHz;

        int count = 0;
        foreach (HostedBot bot in hostedBots.Values)
        {
            if (!bot.InRoom || bot.View == null || bot.View.Eliminated || eliminated.Contains(bot.Slot)) continue;

            // Hider bots are reported once they are at their hiding place: the first report places them.
            if (bot.Role == Role.Fugitive && bot.SpawnIndex != Snapshot.NoSpawn && !hideBots.OnlineHidingDone) continue;

            BotState state = StateOf(bot);
            if (bot.EverSent && state.Equals(bot.Sent) && now - bot.SentAt < BotKeepAliveSeconds) continue;

            bot.Sent = state;
            bot.EverSent = true;
            bot.SentAt = now;
            botStates[count++] = state;
        }

        if (count == 0 && now < botKeepAliveAt) return;
        botKeepAliveAt = now + BotKeepAliveSeconds;
        Net.ReportBots(botStates, count);
    }

    private static readonly int BotBaseColorId = Shader.PropertyToID("_BaseColor");

    private BotState StateOf(HostedBot bot)
    {
        Vector3 feet = bot.View.FeetPosition;
        bool climbing = bot.Role == Role.Hunter && seekBots != null && seekBots.IsClimbing(bot.View.transform);
        var state = new BotState
        {
            Slot = bot.Slot,
            GridX = Quantize.PosToGrid(feet.x),
            GridY = Quantize.PosToGrid(feet.y),
            GridZ = Quantize.PosToGrid(feet.z),
            Yaw = (ushort)Quantize.YawToBits(bot.View.transform.eulerAngles.y),
            Movement = (byte)(climbing ? MovementState.Climbing : MovementState.Grounded),
            // On a wall "Pose" is the climb, which the Climbing flag already says.
            Pose = (ushort)(climbing ? 0 : PoseIdFor(bot.Animator != null ? bot.Animator.GetInteger(PoseHash) : 0)),
            PaintColor = Quantize.ColorToRgb565(1f, 1f, 1f),
        };

        // Single Play's Camouflage paints the hider bot's material with what is around it.
        if (bot.Role == Role.Fugitive && bot.Body != null && bot.Body.material.HasProperty(BotBaseColorId))
        {
            Color c = bot.Body.material.GetColor(BotBaseColorId);
            state.PaintColor = Quantize.ColorToRgb565(c.r, c.g, c.b);
        }

        return state;
    }

    // The bots' controllers set animator poses; the wire carries the shop's pose ids.
    private static int PoseIdFor(int animatorPose)
    {
        if (animatorPose == 0) return 0;
        for (int id = 1; id < 32; id++)
            if (PoseSelectorUI.AnimatorPoseFor(id) == animatorPose) return id;
        return 0;
    }

    // ================= IOnlineBotGame: what the seeker controller asks =================

    public bool IsHuman(Transform target) => target != null && humanHiders.Contains(target);

    public Color ColorOf(Transform human)
    {
        if (human == localBody)
            return paint != null && paint.TryGetAverageBodyColor(out Color average) ? average : Color.white;

        NetEntityView view = human.GetComponent<NetEntityView>();
        if (view == null) return Color.white;
        Quantize.Rgb565ToColor(view.PaintColor, out float r, out float g, out float b);
        return new Color(r, g, b, 1f);
    }

    public Bounds BoundsOf(Transform human)
    {
        SkinnedMeshRenderer[] meshes = human.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if (meshes.Length == 0) return new Bounds(human.position, Vector3.one * 4f);

        Bounds bounds = meshes[0].bounds;                 // a character can be several meshes
        for (int i = 1; i < meshes.Length; i++) bounds.Encapsulate(meshes[i].bounds);
        return bounds;
    }

    public void BotFire(Transform bot, Vector3 origin, Vector3 direction)
    {
        if (slotOfBody.TryGetValue(bot, out byte slot))
            Net.BotFire(slot, origin, direction, WallDistanceAlong(origin, direction, ShotRayLength));
    }

    public Vector3 RescuePoint(Transform bot, int index)
    {
        NetEntityView view = bot.GetComponent<NetEntityView>();
        Vector3? feet = ResolveSpawn(index, true);
        if (view == null || feet == null) return bot.position;
        return feet.Value + (bot.position - view.FeetPosition);
    }

    public void BotSpot(Transform bot, Transform human)
    {
        if (slotOfBody.TryGetValue(bot, out byte slot) && slotOfBody.TryGetValue(human, out byte target))
            Net.BotSpot(slot, target);
    }
}
