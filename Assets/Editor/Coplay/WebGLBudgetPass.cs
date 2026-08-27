using System;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

/// <summary>
/// Applies the non-texture WebGL budget recommendations from the build audit:
///   1. Compress shipped audio to Vorbis (music = Streaming, SFX = CompressedInMemory).
///   2. Tighten WebGL Player Settings: Brotli, data caching, High managed stripping,
///      strip engine code, and (best-effort) disable the Unity splash logo.
///
/// Audio is scoped to folders that actually ship (the _COREHOLD game content and the
/// Creepy_Cat pack whose ambient music was the largest audio asset in the last build),
/// so we don't spend build time reimporting hundreds of MB of unreferenced vendor music.
/// </summary>
public static class WebGLBudgetPass
{
    private static readonly string[] AudioSearchFolders =
    {
        "Assets/_COREHOLD",
        "Assets/Vendor/Creepy_Cat",
    };

    // Clips longer than this are treated as "music" -> Streaming load type.
    private const float MusicSeconds = 8f;

    public static string Execute()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CompressAudio());
        sb.AppendLine();
        sb.AppendLine(TightenPlayerSettings());
        return sb.ToString();
    }

    private static string CompressAudio()
    {
        var guids = AssetDatabase.FindAssets("t:AudioClip", AudioSearchFolders);
        int scanned = 0, changed = 0;

        try
        {
            AssetDatabase.StartAssetEditing();
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                var imp = AssetImporter.GetAtPath(path) as AudioImporter;
                if (imp == null) continue;
                scanned++;

                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                bool isMusic = clip != null && clip.length >= MusicSeconds;

                var s = imp.defaultSampleSettings;
                bool dirty = false;

                if (s.compressionFormat != AudioCompressionFormat.Vorbis)
                { s.compressionFormat = AudioCompressionFormat.Vorbis; dirty = true; }

                float wantQuality = isMusic ? 0.5f : 0.6f;
                if (Math.Abs(s.quality - wantQuality) > 0.001f)
                { s.quality = wantQuality; dirty = true; }

                var wantLoad = isMusic ? AudioClipLoadType.Streaming : AudioClipLoadType.CompressedInMemory;
                if (s.loadType != wantLoad)
                { s.loadType = wantLoad; dirty = true; }

                if (dirty)
                {
                    imp.defaultSampleSettings = s;
                    imp.SaveAndReimport();
                    changed++;
                }
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            AssetDatabase.Refresh();
        }

        return $"Audio: scanned={scanned} changed={changed} (Vorbis; music>= {MusicSeconds}s streamed, SFX compressed-in-memory).";
    }

    private static string TightenPlayerSettings()
    {
        var sb = new StringBuilder();

        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
        sb.AppendLine($"WebGL.compressionFormat = {PlayerSettings.WebGL.compressionFormat}");

        PlayerSettings.WebGL.dataCaching = true;
        sb.AppendLine($"WebGL.dataCaching = {PlayerSettings.WebGL.dataCaching}");

        try
        {
            var webgl = NamedBuildTarget.WebGL;
            PlayerSettings.SetManagedStrippingLevel(webgl, ManagedStrippingLevel.High);
            sb.AppendLine($"WebGL managedStrippingLevel = {PlayerSettings.GetManagedStrippingLevel(webgl)}");
        }
        catch (Exception e) { sb.AppendLine($"managedStrippingLevel: {e.Message}"); }

        try
        {
            PlayerSettings.stripEngineCode = true;
            sb.AppendLine($"stripEngineCode = {PlayerSettings.stripEngineCode}");
        }
        catch (Exception e) { sb.AppendLine($"stripEngineCode: {e.Message}"); }

        // Splash logo removal only takes effect with a Plus/Pro license, but setting it is harmless.
        try
        {
            PlayerSettings.SplashScreen.show = false;
            PlayerSettings.SplashScreen.showUnityLogo = false;
            sb.AppendLine($"SplashScreen.show = {PlayerSettings.SplashScreen.show} (only applies with Plus/Pro license)");
        }
        catch (Exception e) { sb.AppendLine($"SplashScreen: {e.Message}"); }

        AssetDatabase.SaveAssets();
        return "Player settings:\n" + sb;
    }
}
