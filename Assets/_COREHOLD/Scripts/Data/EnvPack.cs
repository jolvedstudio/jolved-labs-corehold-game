using UnityEngine;

namespace Corehold.Data
{
    /// <summary>
    /// The pool of environment props a generated level may dress itself from
    /// (roadmap R25, placed by R28).
    ///
    /// Entries hold **direct prefab references**, not asset-path strings like the
    /// blockout's <c>Place()</c> uses — paths break silently when assets move, and
    /// a generated level has no human watching the console.
    ///
    /// The metadata is not decoration: the placer cannot do its job without it.
    /// <see cref="Entry.footprintRadius"/> is what the route/pad clearance test
    /// measures against, and <see cref="Entry.height"/> is what the sight-line
    /// occlusion test needs — a prop tall enough to break a turret's line to its
    /// covered spans must be rejected, and `HardpointCoverageGizmo` is a pure
    /// distance test that cannot detect that on its own (R28).
    ///
    /// NOTE: <c>Assets/Vendor/</c> is git-ignored, so a pack referencing vendor
    /// prefabs carries dangling GUIDs for anyone without those packages. Decide
    /// per pack whether it is committed or local-only.
    /// </summary>
    [CreateAssetMenu(menuName = "COREHOLD/Env Pack", fileName = "EnvPack_")]
    public class EnvPack : ScriptableObject
    {
        /// <summary>
        /// Which band of the level an entry is meant to fill.
        ///
        /// <see cref="Unassigned"/> is deliberately value 0 so that a freshly dragged-in
        /// prefab does NOT silently claim a real role. Role is intent — where you want
        /// the prop placed — and nothing about the prefab reveals it, so an unset role
        /// has to fail loudly rather than default to something plausible.
        /// </summary>
        public enum PropRole
        {
            /// <summary>Not yet chosen. Rejected by the generate gate — pick one.</summary>
            Unassigned = 0,
            /// <summary>Large, readable, few — the things a player navigates by.</summary>
            Landmark = 1,
            /// <summary>Mid-size structures filling the field between routes.</summary>
            MidField = 2,
            /// <summary>Small scatter. Cheap, numerous, never sight-line relevant.</summary>
            Clutter = 3,
            /// <summary>Far-band silhouettes beyond the playfield (R11's band).</summary>
            Silhouette = 4
        }

        [System.Serializable]
        public struct Entry
        {
            [Tooltip("The prefab to place. A direct reference, so it survives asset moves.")]
            public GameObject prefab;

            [Tooltip("Which band this fills. The placer fills each band deliberately rather than scattering one pool everywhere.")]
            public PropRole role;

            [Tooltip("Horizontal keep-out radius in metres AT SCALE 1, measured about the prefab pivot (the placer multiplies by the chosen scale). REQUIRED — the clearance test measures this against laneHalfWidth + maxBodyRadius off any route centreline, and against the pad keep-out. Use Tools → COREHOLD → Level → Measure Env Pack Metadata rather than typing it: a radius short by 30% looks fine in the inspector and puts a prop in the lane.")]
            public float footprintRadius;

            [Tooltip("Height above the pivot in metres AT SCALE 1 (the placer multiplies by the chosen scale). REQUIRED — the sight-line occlusion test uses it to decide whether this prop breaks a turret's line to a covered span.")]
            public float height;

            [Tooltip("Uniform scale range. The seed picks within it, so variety stays deterministic.")]
            public Vector2 scaleRange;

            [Tooltip("May this sit inside a hairpin pocket? Pockets are where hardpoints live (R27's 10-14 m fold band), so most props should NOT.")]
            public bool allowInFold;
        }

        [Tooltip("Theme this pack represents, set from its folder under Assets/Authoring/EnvPack/. Identifies the pack in generation reports and contact sheets (R31).")]
        public string themeName;

        [Tooltip("Every prop this pack can place, with the metadata the placer needs.")]
        public Entry[] entries;

        [Tooltip("Weather this theme can have. The seed picks one, so an ice map cannot draw desert dust. The blueprint may override this; empty here and empty there means the null preset, which R13 guarantees is pixel-identical to the authored look.")]
        public WeatherPreset[] weatherPool;

        [Header("Ground")]

        [Tooltip("Optional ground object used instead of the built-in primitive plane. Leave empty for the plane. Either way the ground is SIZED FROM THE CAMERA FRUSTUM (R11) — never from the blueprint's playfieldSize, which is what left a void on the shipped map.")]
        public GameObject groundPrefab;

        [Tooltip("Material for the level's ground — used ONLY when groundPrefab is empty. A ground prefab was authored with its own material and UV tiling, so the generator just fits it to the frustum and leaves its look alone. WeatherApplier (R13) captures the live ground material rather than assuming the shipped one, so either path is supported.")]
        public Material groundMaterial;

        [Tooltip("Texture repeats per metre — used ONLY when groundPrefab is empty (a prefab carries its own tiling). The plane is scaled to fit each map's camera solve, so one fixed tiling would stretch by a different amount on every map; this recomputes it from the final size. 0 leaves the material's own tiling alone.")]
        public float groundTilingPerMetre;

        [Tooltip("Near-field ground detail for POV cameras (M-d): a small grayscale texture tiled " +
                 "far denser than the base map. Empty = a generated seamless noise, which already " +
                 "reads well; assign a texture to art-direct it (cracks, gravel, sand ripples).")]
        public Texture2D groundDetail;

        [Tooltip("How strongly the detail overlays the ground (0 = off).")]
        [Range(0f, 1f)] public float groundDetailStrength = 0.35f;

        [Tooltip("How many times denser than the base map the detail tiles.")]
        [Range(2f, 32f)] public float groundDetailScale = 9f;

        [Header("Dressing composition")]

        [Tooltip("Chance that a placed prop seeds a CLUSTER — a few smaller satellites dropped " +
                 "around it with correlated rotation. Clumping is what reads as composed rather " +
                 "than museum-spaced; 0 restores pure scatter.")]
        [Range(0f, 1f)] public float clusterChance = 0.6f;

        [Tooltip("Maximum satellites per cluster, drawn from the pack's Clutter entries.")]
        [Range(0, 6)] public int clusterMaxSatellites = 4;

        [Tooltip("On terrain maps, props tilt up to this many degrees toward the local slope and " +
                 "settle slightly into the ground, so nothing perches. 0 keeps everything bolt upright.")]
        [Range(0f, 20f)] public float slopeTiltMaxDegrees = 10f;

        [Header("Dressing density")]

        [Tooltip("Multiplier on LANDMARK placements inside the design box (1 ≈ 3 on the standard field). 0 disables the role for this theme.")]
        [Range(0f, 4f)] public float landmarkDensity = 1f;

        [Tooltip("Multiplier on MID-FIELD placements (1 ≈ 8 on the standard field).")]
        [Range(0f, 4f)] public float midFieldDensity = 1f;

        [Tooltip("Multiplier on CLUTTER placements (1 ≈ 17 on the standard field).")]
        [Range(0f, 4f)] public float clutterDensity = 1f;

        [Tooltip("Multiplier on the far-band SILHOUETTES beyond the north edge (1 ≈ 8).")]
        [Range(0f, 4f)] public float silhouetteDensity = 1f;

        [Tooltip("Fills the VISIBLE APRON — the frustum-fit ground beyond the design box, where the " +
                 "terrain hills live — with this theme's non-silhouette props: roughly one per 180 m² " +
                 "at 1. The apron used to stay completely bare, which is most of why maps read as " +
                 "empty. 0 restores the bare apron.")]
        [Range(0f, 4f)] public float outfieldDensity = 1f;

        [Header("Look & lighting (M-d)")]

        [Tooltip("Sun override baked at generation. 0 leaves the scene's authored Directional Light untouched — every pre-existing pack behaves exactly as before.")]
        public float sunIntensity;

        [Tooltip("Sun colour, used only when sunIntensity > 0.")]
        public Color sunColor = Color.white;

        [Tooltip("Sun angles in degrees, used only when sunIntensity > 0: X = pitch above the horizon, Y = yaw (compass bearing).")]
        public Vector2 sunAngles = new Vector2(50f, -30f);

        [Tooltip("Scene fog baked at generation as the BASE look. A weather preset still layers its own fog over this at load and restores it on clear (R13 authority is unchanged). Density 0 leaves fog untouched.")]
        public Color fogColor = new Color(0.65f, 0.70f, 0.78f);

        [Tooltip("ExponentialSquared fog density. 0 = leave the scene's fog alone. Values around 0.004-0.010 read as atmospheric depth at this camera distance.")]
        [Range(0f, 0.05f)] public float fogDensity;

        [Tooltip("The worn-path band drawn along every route at generation (M-d route legibility). Alpha 0 disables it. Dark translucent reads as a trodden road over any ground texture.")]
        public Color roadwayColor = new Color(0.03f, 0.03f, 0.04f, 0.35f);

        [Tooltip("Optional full post-processing profile for this theme's maps, replacing the shared default (ACES + bloom + vignette + grade). Weather grades still layer over whichever profile is active.")]
        public UnityEngine.Rendering.VolumeProfile postProfile;

        /// <summary>
        /// Tiling to write for a ground of <paramref name="sizeMetres"/>, or
        /// <c>Vector2.zero</c> when this pack does not manage tiling.
        ///
        /// Whoever applies this must write it through a <c>MaterialPropertyBlock</c>
        /// (<c>_BaseMap_ST</c>), NOT through <c>renderer.material</c> (leaks an instance
        /// per rebuild) and NOT through <c>sharedMaterial</c> (edits the material ASSET,
        /// so one generated map silently retiles every other map using it).
        /// </summary>
        public Vector2 GroundTilingFor(Vector2 sizeMetres)
        {
            if (groundTilingPerMetre <= 0f)
                return Vector2.zero;
            return new Vector2(sizeMetres.x * groundTilingPerMetre,
                               sizeMetres.y * groundTilingPerMetre);
        }

        /// <summary>Count of usable entries in a role (an entry with no prefab is skipped).</summary>
        public int CountInRole(PropRole role)
        {
            if (entries == null)
                return 0;
            int n = 0;
            for (int i = 0; i < entries.Length; i++)
                if (entries[i].prefab != null && entries[i].role == role)
                    n++;
            return n;
        }

        /// <summary>
        /// Entries whose metadata is unusable — a missing prefab, a zero
        /// footprint/height that would make the clearance and occlusion tests
        /// silently pass everything, or an <see cref="PropRole.Unassigned"/> role that
        /// would leave the placer with nowhere to put the prop. Surfaced by the
        /// generate-menu validation so a bad pack fails loudly rather than dressing a
        /// level unsafely.
        /// </summary>
        public int CountInvalid()
        {
            if (entries == null)
                return 0;
            int n = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                Entry e = entries[i];
                if (e.prefab == null || e.footprintRadius <= 0f || e.height <= 0f ||
                    e.role == PropRole.Unassigned)
                    n++;
            }
            return n;
        }
    }
}
