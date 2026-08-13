using UnityEngine;

/// <summary>
/// Hierarchy-independent object lookup for the editor tools.
///
/// A lot of this tooling addresses objects by ROOT-relative path —
/// <c>"RefineryLevel/Core_Blockout/Core_Target"</c>,
/// <c>"Canvas_Menus/BuildMenu"</c> — which <see cref="GameObject.Find(string)"/>
/// only resolves while those first segments sit at the scene root. That made the
/// hierarchy un-reorganisable: grouping roots into containers would silently
/// break the blockout, camera framing, lighting and several validators, with no
/// error beyond a null reference somewhere later.
///
/// <see cref="Find"/> is a strict superset of <c>GameObject.Find</c>: it resolves
/// the FIRST segment by name (which searches the whole scene at any depth), then
/// walks the remainder relatively. Plain names behave exactly as before, so it is
/// a safe drop-in everywhere.
/// </summary>
public static class SceneLookup
{
    /// <summary>
    /// Resolve a name or a root-relative path without caring where the path's
    /// first segment lives in the hierarchy. Returns null when any segment is
    /// missing. Like <c>GameObject.Find</c>, this only sees ACTIVE objects.
    /// </summary>
    public static GameObject Find(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        int slash = path.IndexOf('/');
        string head = slash < 0 ? path : path.Substring(0, slash);

        GameObject root = GameObject.Find(head);
        if (root == null || slash < 0)
            return root;

        Transform child = root.transform.Find(path.Substring(slash + 1));
        return child != null ? child.gameObject : null;
    }
}
