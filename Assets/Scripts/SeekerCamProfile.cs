using UnityEngine;

// NOTE: Only the default constructor was in this paste (field default assignments + base ctor
// call) - no other methods, so this is presumably a plain data/config holder consumed by
// CameraController for the Seeker role's third-person and prone camera framing. Field names are
// confirmed exactly as decompiled; only their defaults are known, not how they're used elsewhere.
//
// RE-VERIFICATION PASS: checked every default value against the raw .ctor — all nine match
// exactly, in the same order. Nothing to fix.
//
// UNRESOLVED: fields are kept private+[SerializeField] (the safe default for an Inspector-tunable
// data holder), but this dump doesn't show CameraController's code confirming whether it needs
// cross-class access to these fields, which would require them to be public instead. Worth
// re-checking once CameraController's raw/draft is available.
public class SeekerCamProfile : MonoBehaviour
{
    [SerializeField] public float tpsSide = -0.07f;
    [SerializeField] public float tpsLookAhead = 10.7f;
    [SerializeField] public float tpsHeight = 4.99f;
    [SerializeField] public float tpsBack = 0.12f;
    [SerializeField] public float proneHeightDrop = 2.5f;
    [SerializeField] public float proneLookDrop = 1.8f;
    [SerializeField] public float tpsLookHeight = 2.5f;
    [SerializeField] public float tpsLookSide = 0.5f;
    [SerializeField] public float proneBack = 0.12f;
}