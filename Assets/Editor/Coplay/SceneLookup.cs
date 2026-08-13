using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Hierarchy-independent, SCENE-SCOPED object lookup for the editor tools.
///
/// A lot of this tooling addresses objects by ROOT-relative path —
/// <c>"RefineryLevel/Core_Blockout/Core_Target"</c>,
/// <c>"Canvas_Menus/BuildMenu"</c> — which <see cref="GameObject.Find(string)"/>
/// only resolves while those first segments sit at the scene root. That made the
/// hierarchy un-reorganisable: grouping roots into containers would silently
/// break the blockout, camera framing, lighting and several validators, with no
/// error beyond a null reference somewhere later.
///
/// <see cref="Find"/> resolves the FIRST segment by name ANYWHERE IN THE ACTIVE
/// SCENE (at any depth), then walks the remainder relatively.
///
/// <b>Active scene only, and that part is load-bearing.</b> <c>GameObject.Find</c>
/// searches every LOADED scene, so with a second scene open — the shipped
/// Game.unity, an additively loaded map — a setup tool asking "is there a
/// GameManager?" gets the OTHER scene's answer and skips creating its own. A
/// generated scene then comes out missing its singletons while every tool
/// reports success. Scoping the search to the active scene is what makes
/// "build into the scene I am building" true.
///
/// Matching <c>GameObject.Find</c>'s other semantics deliberately: only ACTIVE
/// objects are found, so tools that relied on disabled objects staying invisible
/// (the intentionally-disabled refinery props) behave exactly as before.
/// </summary>
public static class SceneLookup
{
    /// <summary>
    /// Resolve a name or a root-relative path within the active scene, without
    /// caring where the path's first segment lives in the hierarchy. Returns null
    /// when any segment is missing. Only ACTIVE objects are visible.
    /// </summary>
    public static GameObject Find(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        int slash = path.IndexOf('/');
        string head = slash < 0 ? path : path.Substring(0, slash);

        GameObject root = FindInActiveScene(head);
        if (root == null || slash < 0)
            return root;

        Transform child = root.transform.Find(path.Substring(slash + 1));
        return child != null ? child.gameObject : null;
    }

    /// <summary>
    /// First ACTIVE object named <paramref name="name"/> anywhere in the active
    /// scene's hierarchy. Depth-first from the scene roots, in root order — the
    /// same order the Hierarchy window shows, so "the first one" is predictable.
    /// </summary>
    private static GameObject FindInActiveScene(string name)
    {
        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid())
            return null;

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            GameObject hit = SearchRecursive(root.transform, name);
            if (hit != null)
                return hit;
        }
        return null;
    }

    private static GameObject SearchRecursive(Transform t, string name)
    {
        if (!t.gameObject.activeInHierarchy)
            return null;                       // GameObject.Find skips inactive; so do we
        if (t.gameObject.name == name)
            return t.gameObject;

        for (int i = 0; i < t.childCount; i++)
        {
            GameObject hit = SearchRecursive(t.GetChild(i), name);
            if (hit != null)
                return hit;
        }
        return null;
    }
}
