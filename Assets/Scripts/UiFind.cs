using System.Collections.Generic;
using UnityEngine;

// Looks up a UI child that must be there. Much of the UI is built in code and found again by
// name (Transform.Find), so renaming an object in a builder silently breaks the code that binds
// it: the label stays empty, the button does nothing. Require does the same lookup, but a missing
// object is logged once, with the full path asked for, so the break shows in the Console.
//
// Use plain Transform.Find where a missing object is expected (the "was it built already?" checks
// and the "Slot0, Slot1, ... until one is missing" loops).
public static class UiFind
{
    private static readonly HashSet<string> reported = new HashSet<string>();

    /// <summary>The child at <paramref name="path"/> under <paramref name="parent"/>; null, logged once, when missing.</summary>
    public static Transform Require(Transform parent, string path)
    {
        if (parent == null)
        {
            Report("<null>/" + path);
            return null;
        }

        Transform found = parent.Find(path);
        if (found == null) Report(PathOf(parent) + "/" + path);
        return found;
    }

    /// <summary>The <typeparamref name="T"/> on the child at <paramref name="path"/>; null, logged once, when either is missing.</summary>
    public static T Require<T>(Transform parent, string path) where T : Component
    {
        Transform found = Require(parent, path);
        if (found == null) return null;

        T component = found.GetComponent<T>();
        if (component == null) Report(PathOf(found) + " (no " + typeof(T).Name + ")");
        return component;
    }

    public static string PathOf(Transform t)
    {
        if (t == null) return "<null>";
        string path = t.name;
        for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
        return path;
    }

    private static void Report(string what)
    {
        if (reported.Add(what)) Debug.LogWarning("[UI] Missing " + what + ": the code that binds it was not updated after a rename?");
    }
}
