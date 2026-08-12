using UnityEngine;

namespace Corehold.Data
{
    /// <summary>
    /// The damage-vs-armour multiplier table (GDD §7.1, §12.2).
    /// Exposes <see cref="Multiplier"/> backed by a 3×3 float grid — rows are
    /// <see cref="DamageType"/> (Kinetic, Energy, Explosive), columns are
    /// <see cref="ArmourType"/> (Unarmoured, Plated, Shielded).
    ///
    /// Unity does not serialize multidimensional arrays, so the grid is stored as
    /// three serializable rows. A dedicated PropertyDrawer (see DamageTableEditor)
    /// renders it as a labelled 3×3 grid in the Inspector.
    /// </summary>
    [CreateAssetMenu(menuName = "COREHOLD/Damage Table", fileName = "DamageTable")]
    public class DamageTable : ScriptableObject
    {
        /// <summary>One row of the table: multipliers of a damage type against each armour type.</summary>
        [System.Serializable]
        public struct Row
        {
            public float vsUnarmoured;
            public float vsPlated;
            public float vsShielded;

            public float this[int armourIndex]
            {
                get
                {
                    switch (armourIndex)
                    {
                        case 0: return vsUnarmoured;
                        case 1: return vsPlated;
                        default: return vsShielded;
                    }
                }
            }
        }

        [Tooltip("Row 0 = Kinetic, Row 1 = Energy, Row 2 = Explosive. Columns: Unarmoured, Plated, Shielded.")]
        [SerializeField]
        private Row[] rows = new Row[3];

        /// <summary>
        /// The damage multiplier for a given damage type against a given armour type.
        /// </summary>
        public float Multiplier(DamageType damage, ArmourType armour)
        {
            int r = (int)damage;
            int c = (int)armour;
            if (rows == null || r < 0 || r >= rows.Length)
                return 1f;
            return rows[r][c];
        }

        private void OnValidate()
        {
            // Keep the grid at exactly 3 rows so the drawer and Multiplier() stay valid.
            if (rows == null || rows.Length != 3)
            {
                var resized = new Row[3];
                if (rows != null)
                {
                    for (int i = 0; i < rows.Length && i < 3; i++)
                        resized[i] = rows[i];
                }
                rows = resized;
            }
        }
    }
}
