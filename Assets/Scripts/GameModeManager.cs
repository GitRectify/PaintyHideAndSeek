using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
// UnityEngine.Object and System.Object are both in scope (using System + using UnityEngine), so a bare
// `Object.` is ambiguous (CS0104). This alias makes every `Object.X` mean UnityEngine.Object.
using Object = UnityEngine.Object;

public class GameModeManager : MonoBehaviour
{
    // NOTE: PlayerRef.Resolve's signature is confirmed here for the first time in this project
    // (it was previously an open compile-blocker) — it takes a name string and returns something
    // usable as a GameObject (its .transform is read right after). Written below as returning
    // GameObject; PlayerRef itself isn't reversed in this session beyond this one call site.

    public bool RoundActive { get; set; }
    public bool PlayerIsSeeker { get; set; }
    public bool PlayerFrozen { get; set; }

    // True only while the player is the seeker AND the hider bots are still in their initial
    // hide/countdown window — used to gate the seeker player from acting until hiders have hidden.
    public bool SeekerHideCountdown
    {
        get
        {
            if (!RoundActive || !PlayerIsSeeker) return false;
            if (hide == null) return false;
            return hide.hideCountdown;
        }
    }

    public string homeDecorName = "ObjectHome";
    public string homeEnvName = "home";

    private BotHideController hide;
    private BotSeekController seek;
    private BotManager botMgr;
    private MatchmakingUI matchmaking;
    private LevelManager levelMgr;
    private ChameleonPaint paint;

    private Transform playerTf;
    private Vector3 playerHomePos;
    private Quaternion playerHomeRot;
    private Vector3 playerHomeScale;
    private bool playerHomeCaptured;

    private Transform homeDecor;
    private Transform homeEnv;

    // FIX (confirmed, traced three times): a prior draft threw here when PlayerRef.Resolve("Player")
    // returned null, with a comment claiming raw does the same. It's actually the opposite. Raw's
    // structure is: `bVar1 = (x != null); if (bVar1) { ...populate playerTf/playerHomePos/etc,
    // goto success...; throw; }` - the throw is INSIDE the `if (x != null)` block, alongside the
    // success path. So if x IS null, that entire block - throw included - is skipped, and execution
    // falls straight through to the continuation with playerTf simply never assigned (stays null,
    // playerHomeCaptured stays false). Raw only throws in the practically-unreachable case where x
    // is valid but the transform/position/rotation/localScale chain somehow yields null partway
    // through. This is not the usual "unreachable defensive check" pattern - PlayerRef.Resolve
    // returning null for a not-yet-spawned player is a real, plausible scenario, and every other
    // method in this class (SetPlayerVisible, ResetPlayerHome, etc.) already null-checks playerTf
    // before use, consistent with it legitimately being allowed to stay unset here.
    private void Start()
    {
        hide = Object.FindFirstObjectByType<BotHideController>();
        seek = Object.FindFirstObjectByType<BotSeekController>();
        botMgr = Object.FindFirstObjectByType<BotManager>();
        matchmaking = Object.FindFirstObjectByType<MatchmakingUI>();
        levelMgr = Object.FindFirstObjectByType<LevelManager>();
        paint = Object.FindFirstObjectByType<ChameleonPaint>();

        GameObject playerObj = PlayerRef.Resolve("Player");
        if (playerObj != null)
        {
            playerTf = playerObj.transform;
            playerHomePos = playerTf.position;
            playerHomeRot = playerTf.rotation;
            playerHomeScale = playerTf.localScale;
            playerHomeCaptured = true;
        }

        homeDecor = FindInSceneByName(homeDecorName);
        homeEnv = FindInSceneByName(homeEnvName);

        EnsureEventSystem();
        ShowMenu();
    }

    // Looks up a named Transform in the active scene, first via GameObject.Find (fast path), then by
    // scanning every loaded Transform (including inactive/prefab ones) and filtering to the one that's
    // actually part of a loaded scene, in case the object starts inactive (GameObject.Find only finds
    // active objects).
    //
    // FIX (confirmed): raw explicitly throws in three cases here that a prior draft instead handled
    // silently - FindObjectsOfTypeAll<Transform>() returning null (return null instead of throw), a
    // loop element being null (continue instead of throw), and t.gameObject being null (continue
    // instead of throw). All three are effectively unreachable in practice, but fixed to match raw's
    // actual (throwing) behavior for consistency, per the same reasoning as Grounding.cs earlier in
    // this session.
    private static Transform FindInSceneByName(string n)
    {
        if (string.IsNullOrEmpty(n)) return null;

        GameObject found = GameObject.Find(n);
        if (found != null) return found.transform;

        Transform[] all = Resources.FindObjectsOfTypeAll<Transform>();
        if (all == null)
        {
            throw new NullReferenceException("Resources.FindObjectsOfTypeAll<Transform>() returned null");
        }

        foreach (Transform t in all)
        {
            if (t == null)
            {
                throw new NullReferenceException("FindInSceneByName: null Transform in scan");
            }
            if (t.name != n) continue;

            GameObject go = t.gameObject;
            if (go == null)
            {
                throw new NullReferenceException("FindInSceneByName: Transform with null gameObject");
            }

            if (go.scene.IsValid())
            {
                return t;
            }
        }

        return null;
    }

    // Same pattern used elsewhere in this project (CameraController): prefer the new Input System's
    // UI module when the package is present, otherwise fall back to the legacy StandaloneInputModule.
    private void EnsureEventSystem()
    {
        if (Object.FindFirstObjectByType<EventSystem>() != null) return;

        GameObject go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();

        System.Type inputSystemUiType =
            System.Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");

        if (inputSystemUiType != null)
        {
            go.AddComponent(inputSystemUiType);
        }
        else
        {
            go.AddComponent<StandaloneInputModule>();
        }
    }

    // Returns to the menu/lobby state: stops both bot controllers, restores ragdolls, resets the
    // player to their captured home transform, closes the paint tool, hides the round HUD, and shows
    // the home decor/environment.
    public void ShowMenu()
    {
        GameController controller = SingletonMonoBehavior<GameController>.Instance;
        if (controller != null)
        {
            controller.ResetPoseReward();
        }

        PlayerFrozen = false;

        if (levelMgr != null)
        {
            levelMgr.SetLevel(0);
            levelMgr.HideRoom();
        }

        string roomName = levelMgr != null ? levelMgr.CurrentRoomName : "Room";
        RoomShadows.DisableCasting(roomName);

        if (hide != null) hide.StopMode();
        if (seek != null) seek.StopMode();

        RestoreAllRagdolls();

        // FIX (confirmed): raw throws if botMgr != null but botMgr.Bots == null - a prior draft's
        // `botMgr != null && botMgr.Bots != null` short-circuit silently skipped that case instead
        // (see BeginRound below for the identical, second occurrence of this bug).
        if (botMgr != null)
        {
            if (botMgr.Bots == null)
            {
                throw new NullReferenceException("botMgr.Bots is null");
            }

            foreach (Transform bot in botMgr.Bots)
            {
                if (bot != null) bot.gameObject.SetActive(false);
            }
        }

        ResetPlayerHome();

        if (paint != null)
        {
            paint.CloseDrawing();
            paint.ResetPaint();
        }

        SetPlayerVisible(false);

        if (homeDecor != null) homeDecor.gameObject.SetActive(true);
        if (homeEnv != null) homeEnv.gameObject.SetActive(true);

        RoundActive = false;
        RootManager.Instance?.SetNumber(0);
    }

    public void StartMode1()
    {
        BeginRound(true);
    }

    public void StartMode2()
    {
        BeginRound(false);
    }

    // Starts a round with the player taking the given role. Splits the bot manager's spawned bot
    // names between hiders and seekers so that, together with the player, the totals match the
    // matchmaking UI's configured hunter/hider slot counts (defaulting to 3 hunters / 5 hiders if no
    // MatchmakingUI is present).
    public void BeginRound(bool playerSeeker)
    {
        PlayerIsSeeker = playerSeeker;
        PlayerFrozen = false;

        SetPlayerVisible(true);

        if (homeDecor != null) homeDecor.gameObject.SetActive(false);
        if (homeEnv != null) homeEnv.gameObject.SetActive(false);

        if (levelMgr != null)
        {
            levelMgr.ApplyActiveRoom();
            string roomName = levelMgr.CurrentRoomName;
            if (hide != null) hide.roomRootName = roomName;
            if (seek != null) seek.roomRootName = roomName;
        }

        string roomNameForShadow;
        if (seek != null) roomNameForShadow = seek.roomRootName;
        else if (hide != null) roomNameForShadow = hide.roomRootName;
        else roomNameForShadow = "Room";
        RoomShadows.DisableCasting(roomNameForShadow);

        NavMesh.RemoveAllNavMeshData();

        if (botMgr != null) botMgr.EnsureSpawned();

        if (hide != null) hide.StopMode();
        if (seek != null) seek.StopMode();

        // FIX (confirmed): same bug as ShowMenu above - raw throws if botMgr != null but
        // botMgr.Bots == null.
        if (botMgr != null)
        {
            if (botMgr.Bots == null)
            {
                throw new NullReferenceException("botMgr.Bots is null");
            }

            foreach (Transform bot in botMgr.Bots)
            {
                if (bot != null) bot.gameObject.SetActive(true);
            }
        }

        RestoreAllRagdolls();
        ResetPlayerHome();

        if (paint != null)
        {
            paint.CloseDrawing();
            paint.ResetPaint();
        }

        int hunterSlots = matchmaking != null ? matchmaking.hunterSlots : 3;
        int hiderSlots = matchmaking != null ? (matchmaking.totalPlayers - matchmaking.hunterSlots) : 5;

        string[] allBotNames = botMgr != null ? botMgr.BotNames : new string[0];

        // The player occupies one hider slot when they're not the seeker, and one hunter slot when
        // they are — the remaining slots (never negative) are filled by bots, in order.
        int hiderBotsNeeded = Mathf.Max(hiderSlots - (playerSeeker ? 0 : 1), 0);
        int hunterBotsNeeded = Mathf.Max(hunterSlots - (playerSeeker ? 1 : 0), 0);

        List<string> hiderBotNames = new List<string>();
        List<string> seekerBotNames = new List<string>();

        if (allBotNames != null)
        {
            for (int i = 0; i < allBotNames.Length; i++)
            {
                if (i < hiderBotsNeeded)
                {
                    hiderBotNames.Add(allBotNames[i]);
                }
                else if (i < hiderBotsNeeded + hunterBotsNeeded)
                {
                    seekerBotNames.Add(allBotNames[i]);
                }
            }
        }

        if (hide != null)
        {
            hide.SetBots(hiderBotNames.ToArray());
            if (hiderBotNames.Count > 0)
            {
                hide.BeginCountdown();
            }
        }

        if (seek != null)
        {
            seek.chasePlayer = !playerSeeker;
            seek.SetBots(seekerBotNames.ToArray());

            List<Transform> seekTargets = new List<Transform>();
            foreach (string name in hiderBotNames)
            {
                GameObject obj = GameObject.Find(name);
                if (obj != null) seekTargets.Add(obj.transform);
            }
            seek.SetTargets(seekTargets);

            if (seekerBotNames.Count > 0)
            {
                seek.StartSeeking();
            }
        }

        RoundActive = true;
        RootManager.Instance?.SetNumber(1);

        Debug.Log("[GameMode] Round start: playerSeeker=" + playerSeeker +
                   " -> hiderBots=" + hiderBotNames.Count +
                   ", seekerBots=" + seekerBotNames.Count +
                   " (of " + allBotNames.Length + " spawned)");
    }

    // FIX (confirmed): raw throws if playerTf != null but GetComponentsInChildren<Renderer>()
    // returns null - a prior draft's `if (renderers == null) return;` silently skipped that case.
    private void SetPlayerVisible(bool v)
    {
        if (playerTf == null) return;

        Renderer[] renderers = playerTf.GetComponentsInChildren<Renderer>(true);
        if (renderers == null)
        {
            throw new NullReferenceException("GetComponentsInChildren<Renderer> returned null");
        }

        foreach (Renderer r in renderers)
        {
            if (r != null) r.enabled = v;
        }
    }

    // FIX (confirmed): raw throws if FindObjectsByType<Ragdoll>() returns null - a prior draft's
    // `if (ragdolls == null) return;` silently skipped that case.
    private void RestoreAllRagdolls()
    {
        Ragdoll[] ragdolls = Object.FindObjectsByType<Ragdoll>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (ragdolls == null)
        {
            throw new NullReferenceException("FindObjectsByType<Ragdoll> returned null");
        }

        foreach (Ragdoll r in ragdolls)
        {
            if (r != null) r.Teardown();
        }
    }

    // Puts the player back at a spawn point (preferring LevelManager's spawn if available) or, failing
    // that, the transform captured on Start(). Temporarily disables the CharacterController while
    // repositioning so the physics system doesn't fight the teleport.
    private void ResetPlayerHome()
    {
        if (playerTf == null) return;

        Vector3 position = Vector3.zero;
        Quaternion rotation = Quaternion.identity;

        bool gotSpawn = false;

        if (levelMgr != null)
        {
            gotSpawn = levelMgr.TryGetSpawn(
                out position,
                out rotation
            );
        }
        
        if (!gotSpawn)
        {
            if (!playerHomeCaptured) return;
            position = playerHomePos;
            rotation = playerHomeRot;
        }

        CharacterController cc = playerTf.GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;

        if (playerHomeCaptured)
        {
            playerTf.localScale = playerHomeScale;
        }

        playerTf.SetPositionAndRotation(position, rotation);

        if (cc != null) cc.enabled = true;
    }

    // Called when the player picks a different level/room from the menu (round not in progress):
    // re-applies the newly selected room and resets the player's home transform for it.
    public void RefreshSelectedLevel()
    {
        if (RoundActive) return;

        if (levelMgr != null)
        {
            if (levelMgr.ApplyActiveRoom())
            {
                NavMesh.RemoveAllNavMeshData();
            }
        }

        ResetPlayerHome();
    }

    public int FugitivesLeft
    {
        get
        {
            int botCount = hide != null ? hide.BotCount : 0;
            return botCount + (PlayerIsSeeker ? 0 : 1);
        }
    }

    public int HuntersLeft
    {
        get
        {
            int botCount = seek != null ? seek.BotCount : 0;
            return botCount + (PlayerIsSeeker ? 1 : 0);
        }
    }

    public int TimerSeconds
    {
        get
        {
            if (PlayerIsSeeker)
            {
                return hide != null ? hide.TimerSeconds : 0;
            }
            return seek != null ? seek.TimerSeconds : 0;
        }
    }

    public float TimerFraction
    {
        get
        {
            if (PlayerIsSeeker)
            {
                return hide != null ? hide.TimerFraction : 0f;
            }
            return seek != null ? seek.TimerFraction : 0f;
        }
    }
}
