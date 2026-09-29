using System.Collections.Generic;
using UnityEngine;

public class HomeShowcaseBots : MonoBehaviour
{
    private static readonly int PoseHash;
    private static readonly int SpeedHash;

    static HomeShowcaseBots()
    {
        PoseHash = Animator.StringToHash("Pose");
        SpeedHash = Animator.StringToHash("Speed");
    }

    public bool showOnHome;
    public string playerName = "Player";
    public GameObject botPrefab;
    public float sideOffset = 10f;
    public float depthOffset;
    public int leftBuddyPose = 1;
    public int rightBuddyPose = 3;
    public int playerPose = 4;

    private List<GameObject> buddies = new List<GameObject>();
    private GameModeManager gmm;
    private BotManager botMgr;

    private void Start()
    {
        if (!showOnHome) return;

        gmm = Object.FindFirstObjectByType<GameModeManager>();
        botMgr = Object.FindFirstObjectByType<BotManager>();

        if (!RoundActive())
        {
            Spawn();
        }
    }

    private bool RoundActive()
    {
        return gmm != null && gmm.RoundActive;
    }

    // Spawns one decorative bot to each side of the player, using botPrefab (falling back to
    // BotManager's own botPrefab if this component's isn't assigned), then poses the player's own
    // Animator too.
    private void Spawn()
    {
        GameObject playerObj = PlayerRef.Resolve(playerName);
        if (playerObj == null) return;

        GameObject prefab = botPrefab != null ? botPrefab : (botMgr != null ? botMgr.botPrefab : null);
        if (prefab == null)
        {
            Debug.LogWarning("[HomeShowcaseBots] No bot prefab — assign 'botPrefab' or BotManager.botPrefab.");
            return;
        }

        Transform playerTf = playerObj.transform;
        Transform parent = playerTf.parent;

        float[] sideOffsets = { -sideOffset, sideOffset };
        int[] poses = { leftBuddyPose, rightBuddyPose };

        for (int i = 0; i < sideOffsets.Length; i++)
        {
            Vector3 playerPos = playerTf.position;
            Vector3 position = new Vector3(playerPos.x + sideOffsets[i], playerPos.y, playerPos.z + depthOffset);
            Quaternion rotation = playerTf.rotation;

            GameObject buddy = Object.Instantiate(prefab, position, rotation, parent);
            buddy.name = "HomeBuddy" + (i + 1);
            buddy.transform.localScale = playerTf.localScale;

            MakeDecoration(buddy);
            buddy.SetActive(true);

            Animator anim = buddy.GetComponentInChildren<Animator>(true);
            SetPose(anim, poses[i]);

            buddies.Add(buddy);
        }

        Animator playerAnim = playerTf.GetComponentInChildren<Animator>(true);
        SetPose(playerAnim, playerPose);

        Debug.Log("[HomeShowcaseBots] Spawned " + buddies.Count + " Home buddy bot(s).");
    }

    private void Update()
    {
        if (!showOnHome) return;

        if (RoundActive())
        {
            if (buddies.Count > 0)
            {
                DestroyBuddies();
            }
        }
        else
        {
            if (buddies.Count == 0)
            {
                Spawn();
            }
        }
    }

    private void DestroyBuddies()
    {
        if (buddies == null) return;

        foreach (GameObject go in buddies)
        {
            if (go != null)
            {
                Object.Destroy(go);
            }
        }

        buddies.Clear();

        GameObject playerObj = PlayerRef.Resolve(playerName);
        if (playerObj != null)
        {
            Animator anim = playerObj.GetComponentInChildren<Animator>(true);
            SetPose(anim, 0);
        }
    }

    // Strips a spawned decorative bot down to a non-interactive showpiece: disables its
    // CharacterController (so it doesn't collide/block) and removes its name tag entirely.
    private static void MakeDecoration(GameObject go)
    {
        if (go == null) return;

        CharacterController cc = go.GetComponent<CharacterController>();
        if (cc != null)
        {
            cc.enabled = false;
        }

        BotNameTag tag = go.GetComponentInChildren<BotNameTag>(true);
        if (tag == null) return;

        tag.enabled = false;
        Object.Destroy(tag);
    }

    private static void SetPose(Animator anim, int pose)
    {
        if (anim == null) return;

        anim.SetFloat(SpeedHash, 0f);

        if (pose > 3) pose = 4;
        if (pose < 0) pose = 0;

        anim.SetInteger(PoseHash, pose);
    }
}