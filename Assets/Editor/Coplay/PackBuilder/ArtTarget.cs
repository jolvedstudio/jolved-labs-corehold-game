using Corehold.Data;
using UnityEngine;

/// <summary>
/// The seam between vision and tooling (EnvPack Builder L1).
///
/// Reading a reference image — "this world is enormous fluted massifs on a
/// bare rose-orange floor" — is semantic work and happens OUTSIDE Unity:
/// Claude reads the references and writes these values (the Wadi Rum ones are
/// created by <see cref="ArtTargetFactory"/>, transcribed from
/// docs/art_direction_wadi_rum.md). Everything downstream — measuring prefabs,
/// scoring them against the bands, writing the EnvPack, staging review scenes —
/// is deterministic editor code that consumes THIS asset.
///
/// That split is the whole architecture: vision produces data, the editor
/// consumes data. No vision model or cloud call ever enters the project, and a
/// new biome is a new ArtTarget, not new code. The one exception lives in
/// <see cref="PaletteExtractor"/>: dominant-color extraction is arithmetic,
/// not semantics, so that single piece of image reading may run in-editor.
/// </summary>
[CreateAssetMenu(menuName = "COREHOLD/Art Target", fileName = "ArtTarget_")]
public class ArtTarget : ScriptableObject
{
    [Tooltip("EnvPack this target builds/updates — must equal that pack's themeName.")]
    public string themeName;

    [Tooltip("The reference images. The record of intent, and the palette extractor's input.")]
    public Texture2D[] referenceImages;

    [Header("Palette")]
    public Color sunColor = Color.white;
    [Tooltip("X = pitch above horizon, Y = yaw. Low pitch = long raking shadows.")]
    public Vector2 sunAngles = new Vector2(35f, -30f);
    public float sunIntensity = 2f;
    public Color fogColor = new Color(0.65f, 0.70f, 0.78f);
    [Tooltip("ExponentialSquared. 0.002 ≈ clear play area, blue distance.")]
    public float fogDensity = 0.002f;

    [Tooltip("MATCHING target, not a render value: what the ground reads as in the references. The scorer measures prefab colors against these.")]
    public Color groundTint = new Color(0.80f, 0.55f, 0.38f);
    [Tooltip("MATCHING target: what the rock reads as in the references.")]
    public Color rockTint = new Color(0.72f, 0.50f, 0.40f);

    [Header("Scale ladder")]
    [Tooltip("The size tiers the references demand. Each band says what to look for (heights, proportions, name hints), how many DISTINCT prefabs it wants, and the scaleRange written onto its picks.")]
    public Band[] bands;

    [System.Serializable]
    public struct Band
    {
        [Tooltip("Display name, e.g. Massif.")]
        public string name;
        [Tooltip("EnvPack role its entries land in. Massifs go to Silhouette — a 40 m footprint offered in-field burns attempts against gates that always refuse it.")]
        public EnvPack.PropRole role;
        [Tooltip("Authored height window in metres, BEFORE scaleRange.")]
        public float minHeight;
        public float maxHeight;
        [Tooltip("Written onto picked entries — the fix for a pack authored entirely at (1,1).")]
        public Vector2 scaleRange;
        [Tooltip("Distinct prefabs wanted. Unfilled remainder becomes the shopping list.")]
        public int wantDistinct;
        [Tooltip("height / width window. A massif is 0.4–2.5; a spire is 3+.")]
        public float aspectMin;
        public float aspectMax;
        [Tooltip("Name substrings that earn a score bonus.")]
        public string[] nameTokens;
    }

    [Header("Written to the EnvPack")]
    public float landmarkDensity = 1f;
    public float midFieldDensity = 1f;
    public float clutterDensity = 1f;
    public float silhouetteDensity = 1f;
    public float outfieldDensity = 1f;
    public float clusterChance = 0.6f;
    public float scaleJitter = 0.25f;
    public float toneVariation = 0.35f;
    public float slopeTiltMaxDegrees = 10f;
    public float groundZoneStrength = 0.6f;

    [Tooltip("Replaces the pack's weather pool. Duplicates weight the draw — [Clear, Clear, Dust] is 2:1 clear.")]
    public WeatherPreset[] weatherPool;

    [Header("Ground — written only when this is ticked")]
    [Tooltip("OFF by default so the builder never clobbers hand-tuned ground. Tick it when the " +
             "target owns the ground look — e.g. after buying a texture set for this biome — and " +
             "the fields below replace the pack's. Skybox and post profile always stay the pack's.")]
    public bool overrideGroundTextures;

    [Tooltip("Base ground material. Wants a SEAMLESS, LOW-CONTRAST albedo: the substrate tint and " +
             "both detail lanes multiply over it, so contrast in the base map fights all of them " +
             "and reads as tiling.")]
    public Material groundMaterial;

    [Tooltip("Texture repeats per metre. 0.2 = a repeat every 5 m.")]
    public float groundTilingPerMetre = 0.2f;

    [Tooltip("Fine near-field detail, GRAYSCALE, where 0.5 is neutral. Sand ripples, grain. " +
             "Empty = a generated noise, which already works.")]
    public Texture2D groundDetail;

    [Range(0f, 1f)] public float groundDetailStrength = 0.35f;
    [Range(2f, 32f)] public float groundDetailScale = 9f;

    [Tooltip("Coarse detail for ROCKY ground (E2) — gravel, scree. Grain size is most of what " +
             "separates gravel from sand at this camera distance. Empty = a generated coarse noise.")]
    public Texture2D groundRockDetail;

    [Header("Builder")]
    [Tooltip("Hard cap on total pack entries (existing + new picks).")]
    public int maxEntries = 50;

    [Tooltip("EXACTLY the roots the prefab indexer scans — each one recursively (every subfolder), " +
             "nothing outside them, no silent defaults. If a root is missing on this machine the scan " +
             "says so loudly. Asset Store packs import to Assets/<PackName> by default: move them " +
             "under Assets/Vendor (git-ignored, per vendor policy) or add their folder here. Picks " +
             "from vendor roots are flagged NEEDS LOCALIZING in the report.")]
    public string[] scanFolders =
    {
        "Assets/Vendor",
        "Assets/Authoring/EnvPack",
        "Assets/_COREHOLD/Authoring/EnvPack",
    };
}
