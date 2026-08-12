using UnityEngine;
using Corehold.Data;

namespace Corehold.UI
{
    /// <summary>
    /// Central holder for the handful of shared UI assets the controllers need at
    /// runtime (GDD §9.7) — a font, two sizes, the cyan V2 sprite set (kept under a
    /// dozen), the armour-pip colours, and the turret/enemy definition catalogues so
    /// menus and the wave preview can be built from data.
    ///
    /// Populated once by the editor build script and read by every UI controller,
    /// so there is a single place that owns "which sprite, which colour" and the
    /// dozen-sprite budget is visibly enforced.
    /// </summary>
    [DisallowMultipleComponent]
    public class UITheme : MonoBehaviour
    {
        public static UITheme Instance { get; private set; }

        [Header("Type (GDD §9.7 — one font, two sizes)")]
        public TMPro.TMP_FontAsset font;
        public float fontSizeLarge = 34f;
        public float fontSizeSmall = 22f;

        [Header("Cyan V2 sprite set (GDD §9.7 — nine-sliced, ≤ a dozen)")]
        public Sprite panel;         // main nine-sliced panel frame
        public Sprite popup;         // popup / dialog frame
        public Sprite buttonNormal;  // button (cyan) normal
        public Sprite buttonPressed; // button (cyan) pressed
        public Sprite buttonDisabled;// button (gray) disabled
        public Sprite barBackground; // health/integrity bar bg
        public Sprite barFill;       // health/integrity bar fill
        public Sprite pauseIcon;     // pause glyph
        public Sprite starFull;      // victory star (light)
        public Sprite starEmpty;     // victory star (gray)

        [Header("Faction / counter colours")]
        public Color cyan = new Color(0.20f, 0.85f, 1f, 1f);
        public Color amber = new Color(1f, 0.6f, 0.1f, 1f);
        public Color danger = new Color(1f, 0.3f, 0.3f, 1f);

        [Header("Armour pip colours (GDD §7.1, §9.4)")]
        public Color unarmoured = new Color(0.75f, 0.78f, 0.8f, 1f);
        public Color plated = new Color(0.95f, 0.78f, 0.25f, 1f);
        public Color shielded = new Color(0.35f, 0.7f, 1f, 1f);

        [Header("Catalogues")]
        [Tooltip("The five turret definitions in menu order (GDD §7.2).")]
        public TowerDefinition[] turrets;
        [Tooltip("The damage-vs-armour table shown on the tower panel (GDD §7.1).")]
        public DamageTable damageTable;

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        /// <summary>Colour for an armour type's pip/label (GDD §7.1).</summary>
        public Color ArmourColor(ArmourType armour)
        {
            switch (armour)
            {
                case ArmourType.Plated: return plated;
                case ArmourType.Shielded: return shielded;
                default: return unarmoured;
            }
        }

        /// <summary>Short display letter for an armour type (U / P / S).</summary>
        public static string ArmourLetter(ArmourType armour)
        {
            switch (armour)
            {
                case ArmourType.Plated: return "P";
                case ArmourType.Shielded: return "S";
                default: return "U";
            }
        }
    }
}
