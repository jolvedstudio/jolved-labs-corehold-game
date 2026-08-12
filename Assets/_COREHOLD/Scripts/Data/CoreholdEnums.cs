namespace Corehold.Data
{
    /// <summary>
    /// The three damage types a turret can deal (GDD §7.1).
    /// </summary>
    public enum DamageType
    {
        Kinetic,
        Energy,
        Explosive
    }

    /// <summary>
    /// The three armour types an enemy can carry (GDD §7.1).
    /// </summary>
    public enum ArmourType
    {
        Unarmoured,
        Plated,
        Shielded
    }

    /// <summary>
    /// Per-turret target selection priority (GDD §7.4).
    /// First = furthest along the route (default).
    /// </summary>
    public enum TargetPriority
    {
        First,
        Closest,
        Strongest
    }

    /// <summary>
    /// Difficulty tiers (GDD §2.3). Applied as a struct over LevelDefinition at run start.
    /// </summary>
    public enum Difficulty
    {
        Normal,
        Veteran,
        Nightmare
    }
}
