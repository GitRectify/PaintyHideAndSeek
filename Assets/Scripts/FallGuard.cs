using UnityEngine;

/// <summary>
/// A floor under everyone. If the player ever ends up below the room — a gap in a floor, a
/// spawn over nothing, a fall off a wall at the edge — they are put back on the nearest spawn
/// marker of their side, standing, with the fall's speed gone. Online the move is the client's
/// own, as every move is, so the others simply see the player back on a spawn.
/// Created at start and kept for the session; it only acts during a round.
/// </summary>
public sealed class FallGuard : MonoBehaviour
{
    private static FallGuard instance;

    private GameModeManager gmm;
    private LevelManager levels;
    private JoystickMover mover;

    /// <summary>How many times a player was caught falling out this session (the play test reads it).</summary>
    public static int Rescues { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        if (instance != null) return;
        var go = new GameObject("FallGuard");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<FallGuard>();
    }

    private void Update()
    {
        if (gmm == null) gmm = FindAnyObjectByType<GameModeManager>();
        if (gmm == null || !gmm.RoundActive) return;

        Transform player = PlayerRef.Active;
        if (player == null || !player.gameObject.activeInHierarchy) return;
        if (player.position.y >= RoomSetup.FallFloorY) return;

        if (levels == null) levels = FindAnyObjectByType<LevelManager>();
        GameObject room = levels != null ? levels.CurrentRoomInstance : null;
        if (!RoomSetup.TrySpawn(room, gmm.PlayerIsSeeker, NearestMarker(room, player.position), out Vector3 feet, out Quaternion facing)) return;

        CharacterController cc = player.GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;
        player.SetPositionAndRotation(RoomSetup.PivotForFeet(player, feet), facing);
        if (cc != null) cc.enabled = true;

        if (mover == null) mover = FindAnyObjectByType<JoystickMover>(FindObjectsInactive.Include);
        if (mover != null) mover.StopFalling();

        Rescues++;
        Debug.LogWarning($"[FallGuard] The player fell out of {room.name}; back on a spawn at {feet}.");
    }

    // The marker of the player's side closest under where they fell, by its number.
    private int NearestMarker(GameObject room, Vector3 from)
    {
        if (room == null) return 0;
        string prefix = gmm.PlayerIsSeeker ? "HunterSpawn" : "HiderSpawn";

        var markers = new System.Collections.Generic.List<Transform>();
        foreach (Transform t in room.GetComponentsInChildren<Transform>(true))
            if (t.name.StartsWith(prefix, System.StringComparison.Ordinal)) markers.Add(t);
        markers.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

        int best = 0;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < markers.Count; i++)
        {
            Vector3 d = markers[i].position - from;
            float flat = d.x * d.x + d.z * d.z;
            if (flat < bestDistance) { bestDistance = flat; best = i; }
        }

        return best;
    }
}
