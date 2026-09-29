using System.Collections.Generic;
using UnityEngine;

public class BotManager : MonoBehaviour
{
    [Header("Spawning")]
    [Tooltip("The bot prefab to spawn (must have an Animator + CharacterController + SkinnedMeshRenderer + BotNameTag).")]
    public GameObject botPrefab;

    [Tooltip("How many bots to spawn for the round.")]
    [Range(1, 8)]
    public int botCount = 4;

    [Tooltip("Spawn positions. Bots cycle through these; extras are nudged so they don't overlap.")]
    public Transform[] spawnPoints;

    [Tooltip("Spawned bots are parented under this scene object (for a tidy hierarchy).")]
    public string charactersParentName = "Characters";

    [Header("Player-style names floated above each bot")]
    public string[] nicknamePool =
    {
        "ProSniper",
        "xX_Reaper_Xx",
        "NoobSlayer",
        "QuickScope",
        "BushCamper",
        "GhostFox",
        "ToxicAce",
        "EzClap",
        "MLG_Mike",
        "SilentBob",
        "RageQuit",
        "HeadHunter",
        "Phantom",
        "Blaze"
    };

    private readonly List<Transform> bots = new List<Transform>();
    private readonly Dictionary<Transform, BotNameTag> tags = new Dictionary<Transform, BotNameTag>();
    private bool spawned;

    public IReadOnlyList<Transform> Bots => bots;

    public bool Spawned => spawned;

    public string[] BotNames
    {
        get
        {
            string[] result = new string[bots.Count];
            for (int i = 0; i < bots.Count; i++)
            {
                result[i] = bots[i].name;
            }
            return result;
        }
    }

    public void EnsureSpawned()
    {
        if (spawned) return;
        spawned = true;

        MatchmakingUI matchmakingUI = Object.FindFirstObjectByType<MatchmakingUI>();
        if (matchmakingUI != null)
        {
            int count = matchmakingUI.hunterSlots + matchmakingUI.FugitiveCount - 1;
            if (count > 15) count = 16;
            if (count < 2) count = 1;
            botCount = count;
        }

        Spawn();
        string[] botNames = BotNames;

        BotHideController botHideController = Object.FindFirstObjectByType<BotHideController>();
        BotSeekController botSeekController = Object.FindFirstObjectByType<BotSeekController>();

        if (botHideController != null)
        {
            botHideController.SetBots(botNames);
        }
        if (botSeekController != null)
        {
            botSeekController.SetBots(botNames);
        }

        Debug.Log("[BotManager] Spawned " + bots.Count + " bot(s) on match start.");
    }

    private void Spawn()
    {
        if (botPrefab == null)
        {
            Debug.LogWarning("[BotManager] No botPrefab assigned — no bots spawned.");
            return;
        }

        GameObject charactersParent = GameObject.Find(charactersParentName);
        Transform parent = (charactersParent != null) ? charactersParent.transform : null;

        List<string> pool = new List<string>(nicknamePool);

        for (int i = 0; i < botCount; i++)
        {
            Vector3 position = SpawnPos(i);
            Quaternion rotation = SpawnRot(i);
            GameObject instance = Object.Instantiate(botPrefab, position, rotation, parent);

            instance.name = "Bot" + (i + 1);
            instance.SetActive(true);
            bots.Add(instance.transform);

            BotNameTag nameTag = instance.GetComponentInChildren<BotNameTag>(true);
            if (nameTag != null)
            {
                string name;
                if (pool.Count < 1)
                {
                    name = "Player" + (i + 1);
                }
                else
                {
                    int idx = Random.Range(0, pool.Count);
                    name = pool[idx];
                    pool.RemoveAt(idx);
                }

                nameTag.SetName(name);
                tags[instance.transform] = nameTag;
            }
        }
    }

    private Vector3 SpawnPos(int i)
    {
        if (spawnPoints != null && spawnPoints.Length != 0)
        {
            Transform point = spawnPoints[i % spawnPoints.Length];
            if (point != null)
            {
                int wrapCount = i / spawnPoints.Length;
                if (wrapCount == 0)
                {
                    return point.position;
                }
                return point.position + new Vector3(Mathf.Cos(i), 0f, Mathf.Sin(i)) * 6f;
            }
        }
        return transform.position + new Vector3(Mathf.Cos(i), 0f, Mathf.Sin(i)) * 10f;
    }

    private Quaternion SpawnRot(int i)
    {
        if (spawnPoints != null && spawnPoints.Length != 0)
        {
            Transform point = spawnPoints[i % spawnPoints.Length];
            if (point != null)
            {
                return point.rotation;
            }
        }
        return Quaternion.identity;
    }

    public void SetTagVisible(Transform bot, bool v)
    {
        if (bot != null && tags.TryGetValue(bot, out BotNameTag tag))
        {
            if (tag != null)
            {
                tag.SetVisible(v);
            }
        }
    }

    public void SetAllTagsVisible(bool v)
    {
        foreach (BotNameTag tag in tags.Values)
        {
            if (tag != null)
            {
                tag.SetVisible(v);
            }
        }
    }
}
