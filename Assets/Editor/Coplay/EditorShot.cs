using UnityEngine;

/// <summary>
/// The one way editor tools photograph a scene camera. Extracted from
/// ContactSheet so the coming lookdev stager (EnvPack builder review scenes)
/// shoots through the same code path instead of growing a second copy.
///
/// The camera is borrowed and handed back — target texture restored — because
/// a capture that quietly mutates the thing it measures is how measurement
/// tools start lying. See ContactSheet.ShootGameView for the doctrine.
/// </summary>
internal static class EditorShot
{
    /// <summary>Render <paramref name="cam"/> once at w×h and return the pixels
    /// as an RGB24 texture. The caller owns (and must destroy) the result.
    ///
    /// The result is marked HideAndDontSave because the editor DESTROYS
    /// non-asset objects without that flag on scene operations — the lookdev
    /// stager held ten captures across ten NewScene calls and every one of
    /// them was dead by sheet-composition time (MissingReferenceException in
    /// the field). Callers that cycle scenes should still prefer copying
    /// GetPixels() out immediately: managed arrays are beyond Unity's reach.</summary>
    internal static Texture2D Capture(Camera cam, int w, int h)
    {
        var rt = new RenderTexture(w, h, 24);
        var shot = new Texture2D(w, h, TextureFormat.RGB24, false)
        { hideFlags = HideFlags.HideAndDontSave };
        RenderTexture previousTarget = cam.targetTexture;
        RenderTexture previousActive = RenderTexture.active;
        try
        {
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            shot.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            shot.Apply();
        }
        finally
        {
            RenderTexture.active = previousActive;
            cam.targetTexture = previousTarget;
            Object.DestroyImmediate(rt);
        }
        return shot;
    }
}
