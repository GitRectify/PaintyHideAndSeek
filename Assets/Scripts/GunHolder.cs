using UnityEngine;

public class GunHolder : MonoBehaviour
{
    // NOTE: this resolves the "GunHolder class fields" item that was on the open-caveats list from
    // earlier in this project — all fields below come directly from the constructor, not guesses.
    // Confirmed via IEEE-754 decode: the raw ctor writes an 8-byte value spanning
    // localPosition.z and (via struct-layout spillover) localEuler.x - low 32 bits decode to
    // -1.23 (localPosition.z) and high 32 bits decode to -84.97 (localEuler.x), both consistent
    // with the values below.
    //
    // NOTE: handBoneName, localPosition, localEuler, localScale, and gunStates are all set here but
    // never read by any of the methods in this paste (Attach() only finds a child transform named
    // "gun" by name search — it never parents it to the hand bone or applies these offsets). They're
    // presumably consumed by another method not included in this dump — likely something that
    // actually attaches/positions the gun under the named hand bone, and something that switches the
    // Animator between the "gun_attack"/"gun_walking" states in gunStates. Paste that method if you
    // have it.

    public string characterRootName = "Player";
    public string handBoneName = "mixamorig:RightHand";
    public string gunObjectName = "gun";

    public Vector3 localPosition = new Vector3(-0.07f, -0.18f, -1.23f);
    public Vector3 localEuler = new Vector3(-84.97f, 0f, 0f);
    public Vector3 localScale = new Vector3(10f, 10f, 10f);

    public string[] gunStates = { "gun_attack", "gun_walking" };

    public bool forceVisible;
    public Transform gun;
    private Animator anim;

    public Transform Gun => gun;

    public void SetForceVisible(bool v)
    {
        forceVisible = v;
    }

    private void Start()
    {
        Attach();
        SetVisible(false);
    }

    // Resolves the character to search under (itself, if it already has an Animator in its children —
    // e.g. this GunHolder lives directly on a bot prefab — otherwise the named external root, e.g.
    // "Player"), then finds the first child transform named gunObjectName under it.
    //
    // FIX (confirmed): raw throws (falls through to the function's shared NRE-throw target) if
    // GetComponentsInChildren<Transform>() returns null - there is no silent-return path for that
    // case in raw, unlike the "characterRoot not found" and "gun not found" cases which do
    // explicitly warn-and-return. A prior draft had `if (allChildren == null) return;` here,
    // substituting a silent no-op for raw's actual throw. Practically unreachable (Unity's
    // GetComponentsInChildren never returns null), but fixed to match by simply dropping the
    // guard - `foreach` on a null array throws its own NRE naturally, producing the same crash
    // raw does, consistent with the "let it crash naturally" pattern used throughout this codebase.
    private void Attach()
    {
        Animator existingAnim = GetComponentInChildren<Animator>(true);

        GameObject characterRoot = existingAnim != null
            ? gameObject
            : PlayerRef.Resolve(characterRootName);

        if (characterRoot == null)
        {
            Debug.LogWarning("[GunHolder] '" + characterRootName + "' not found.");
            return;
        }

        anim = characterRoot.GetComponentInChildren<Animator>(true);
        gun = null;

        Transform[] allChildren = characterRoot.GetComponentsInChildren<Transform>(true);

        foreach (Transform t in allChildren)
        {
            if (t == null) continue;
            if (t.name == gunObjectName)
            {
                gun = t;
                break;
            }
        }

        if (gun == null)
        {
            Debug.LogWarning("[GunHolder] gun '" + gunObjectName + "' not found under " + characterRoot.name + ".");
        }
    }

    private void SetVisible(bool v)
    {
        if (gun == null) return;

        GameObject go = gun.gameObject;
        if (go.activeSelf == v) return;

        go.SetActive(v);
    }

    private void LateUpdate()
    {
        if (gun == null) return;
        SetVisible(forceVisible);
    }
}