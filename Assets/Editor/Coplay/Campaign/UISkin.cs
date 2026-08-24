using TMPro;
using UnityEngine;
using UnityEngine.Serialization;

namespace CoreholdEditor.Campaign
{
    /// <summary>
    /// A campaign's UI identity (plan follow-up to §A.5): the palette and font
    /// the scene builders bake into every scene they touch. Campaigns target
    /// different audiences — candy-bright for kids, grim monochrome for adults —
    /// and the LOOK is authored here once per campaign rather than edited into
    /// each scene.
    ///
    /// How it flows: <see cref="Active"/> is an ambient editor-time setting.
    /// The Campaign Builder sets it from the authoring asset around generation
    /// and menu-scene builds; <c>BuildRealUI</c> (called by the pipeline's
    /// skeleton stage) and <c>BuildCampaignScenes</c> read their palette through
    /// it. With no skin active every builder uses its historical constants, so
    /// existing output is byte-identical — a skin is opt-in per campaign.
    ///
    /// Editor asset on purpose: skins are consumed at BUILD time; scenes ship
    /// with the values baked into their UITheme and widgets, so nothing here
    /// needs to exist in a player.
    /// </summary>
    [CreateAssetMenu(menuName = "COREHOLD/UI Skin (editor)", fileName = "Skin_")]
    public class UISkin : ScriptableObject
    {
        /// <summary>The skin the scene builders consult right now. Null = the
        /// historical default look. Set/cleared by the Campaign Builder.</summary>
        public static UISkin Active;

        [Header("Palette — the three roles every widget derives from")]
        [Tooltip("The signature color: titles, buttons, score text (the shipped look's cyan).")]
        public Color accent = new Color(0.20f, 0.85f, 1f, 1f);

        [Tooltip("Secondary highlights: records, warnings, the Continue button (the shipped amber).")]
        public Color warm = new Color(1f, 0.6f, 0.1f, 1f);

        [Tooltip("Damage, defeat, danger states (the shipped red).")]
        public Color danger = new Color(1f, 0.3f, 0.3f, 1f);

        [Tooltip("Secondary in-game label text: turret role tags, upgrade costs, the title's build tag. " +
                 "Was a scattered hardcoded pale blue — promoted to a role so it moves with the skin.")]
        public Color textMuted = new Color(0.8f, 0.9f, 0.95f, 1f);

        [Tooltip("Dark scrims and unskinned panel fills: the Strike Wing cooldown wipe, the boss-bar " +
                 "backing, the settings dim, a panel with no sprite. Alpha is applied per use.")]
        public Color scrim = new Color(0.04f, 0.06f, 0.09f, 1f);

        [Tooltip("The boss health bar's fill — deliberately its own role: it must read as THREAT, not as " +
                 "the accent, whatever the campaign's palette is.")]
        public Color boss = new Color(1f, 0.35f, 0.25f, 1f);

        [Header("Surfaces (menu scenes)")]
        [Tooltip("Menu-scene background (the Welcome/Closing camera clear color).")]
        public Color background = new Color(0.043f, 0.062f, 0.086f);

        [Tooltip("Button/panel fill in menu scenes.")]
        [FormerlySerializedAs("panel")] // renamed: the Sprite slot below owns 'panel', mirroring UITheme
        public Color panelColor = new Color(0.075f, 0.11f, 0.15f);

        [Tooltip("De-emphasised text in menu scenes.")]
        public Color textDim = new Color(0.62f, 0.72f, 0.78f);

        [Header("Type")]
        [Tooltip("Optional font override for everything the builders create. Null = the project's default UI font.")]
        public TMP_FontAsset font;

        [Tooltip("Multiplies both UI text sizes (shipped 34/22). A kids skin wants ~1.25; a dense tactical " +
                 "one can go below 1. Applied at bake time, so re-generate to see it.")]
        [Range(0.6f, 2f)] public float textScale = 1f;

        [Header("Proportions — how CHUNKY the UI is")]
        [Tooltip("Scales the whole UI against the screen (via the canvas reference resolution). 1.2 makes " +
                 "every panel, button and label 20% larger relative to the screen — the single most " +
                 "effective knob for casual/kids art, which needs more room than slim sci-fi art.")]
        [Range(0.7f, 1.8f)] public float uiScale = 1f;

        [Tooltip("Extra pixels added to every built BUTTON's width and height. Fat 9-sliced kit art has " +
                 "thick borders that eat the label's room; this gives it back without moving anything else.")]
        [Range(0f, 48f)] public float buttonPadding = 0f;

        [Tooltip("Corner roundness of the PROCEDURAL fill the builders use where no sprite slot is filled: " +
                 "0 = square, 1 = fully rounded (pill). Shipped look is 0.56. Round for casual, near-0 for " +
                 "hard-edged grim. Ignored wherever a skin sprite fills the slot — that art carries its own " +
                 "shape. Each distinct value generates its own small sprite asset.")]
        [Range(0f, 1f)] public float cornerRoundness = 0.5625f;

        [Header("Sprite slots — the SHAPE language (v2)")]
        [Tooltip("Every slot maps 1:1 onto a UITheme sprite field. Null = the builder's default for that slot " +
                 "(kit path or procedural), so a skin overrides only what it cares about. Fill these from a UI " +
                 "kit with Tools → COREHOLD → Campaign → Create Skin From UI Kit, which copies the chosen " +
                 "sprites into a COMMITTED folder — a skin must never reference git-ignored vendor files.")]
        public Sprite panel;            // main nine-sliced panel frame
        public Sprite popup;            // popup / dialog frame
        public Sprite buttonNormal;
        public Sprite buttonPressed;
        public Sprite buttonDisabled;
        public Sprite barBackground;    // health/integrity bar bg
        public Sprite barFill;
        public Sprite pauseIcon;
        public Sprite starFull;
        public Sprite starEmpty;

        [Tooltip("Who this skin is for, e.g. 'kids — bright, rounded, high-saturation'. Notes only.")]
        [TextArea] public string audience;
    }
}
