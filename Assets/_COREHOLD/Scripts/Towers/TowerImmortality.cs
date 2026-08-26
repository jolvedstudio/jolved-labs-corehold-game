using System.Collections.Generic;
using Corehold.Data;

namespace Corehold.Towers
{
    /// <summary>
    /// Debug immortality for turrets, BY TOWER TYPE (testing aid, GDD §12.4).
    ///
    /// Keyed on <see cref="TowerDefinition.id"/> rather than on turret
    /// instances, and that is the whole design decision: a battleplay test runs
    /// across waves during which turrets are sold, rebuilt, relocated (M-c) and
    /// newly built. A per-instance flag would evaporate at the first rebuild and
    /// would never reach the turret you place on wave 6. A type rule holds for
    /// the whole session — "Autocannons cannot die" stays true however many
    /// Autocannons come and go.
    ///
    /// What immortality does NOT change is as important as what it does: enemies
    /// still acquire and fire at these turrets, the shots still land, aggro and
    /// enemy DPS-on-target are unchanged. Only the health subtraction is skipped
    /// (<see cref="TowerHealth.TakeDamage"/>). So the fight you observe is the
    /// real fight minus the turret loss — which is exactly what you want when
    /// asking "does this composition hold if the front line survives?".
    ///
    /// SHIP SAFETY: this class is always compiled because TowerHealth references
    /// it, but only the editor/development-build console ever populates it. The
    /// <see cref="Any"/> fast path means a shipped build pays one bool test per
    /// damage event and nothing else.
    ///
    /// The state deliberately SURVIVES scene loads: testing a campaign means
    /// crossing level boundaries, and a cheat that silently reset every stage
    /// would be useless there. Because it survives, the debug console draws a
    /// permanent on-screen banner while any type is immortal — a cheat that
    /// alters survivability must never be forgettable while someone is judging
    /// balance.
    /// </summary>
    public static class TowerImmortality
    {
        private static readonly HashSet<string> Ids = new HashSet<string>();

        /// <summary>True when at least one tower type is immortal. The fast path
        /// every damage event tests before doing any real work.</summary>
        public static bool Any => Ids.Count > 0;

        /// <summary>Is this definition's type currently immortal?</summary>
        public static bool IsImmortal(TowerDefinition def) =>
            Ids.Count > 0 && def != null && !string.IsNullOrEmpty(def.id) && Ids.Contains(def.id);

        /// <summary>
        /// Flip one type. Returns the NEW state. Turning a type immortal heals
        /// its live turrets to full: a turret frozen at 8% health reads as
        /// "about to die" for the rest of the test, which is a misleading thing
        /// to stare at while judging a wave.
        /// </summary>
        public static bool Toggle(TowerDefinition def)
        {
            if (def == null || string.IsNullOrEmpty(def.id))
                return false;

            bool on = !Ids.Contains(def.id);
            if (on)
            {
                Ids.Add(def.id);
                HealLive(def.id);
            }
            else
            {
                Ids.Remove(def.id);
            }
            return on;
        }

        /// <summary>Blanket set/clear over a roster (the console's "ALL TYPES").</summary>
        public static void SetAll(IReadOnlyList<TowerDefinition> roster, bool on)
        {
            Ids.Clear();
            if (!on || roster == null)
                return;
            for (int i = 0; i < roster.Count; i++)
            {
                TowerDefinition def = roster[i];
                if (def == null || string.IsNullOrEmpty(def.id))
                    continue;
                Ids.Add(def.id);
                HealLive(def.id);
            }
        }

        public static void Clear() => Ids.Clear();

        /// <summary>Compact "a, b, c" for the console overlay and banner.</summary>
        public static string Describe()
        {
            if (Ids.Count == 0)
                return "none";
            var names = new List<string>(Ids);
            names.Sort(System.StringComparer.Ordinal);
            return string.Join(", ", names);
        }

        /// <summary>Refill every live turret of a type (see <see cref="Toggle"/>).</summary>
        private static void HealLive(string id)
        {
            for (int i = 0; i < Tower.Live.Count; i++)
            {
                Tower t = Tower.Live[i];
                if (t == null || t.Definition == null || t.Definition.id != id)
                    continue;
                var health = t.GetComponent<TowerHealth>();
                if (health != null)
                    health.Heal();
            }
        }
    }
}
