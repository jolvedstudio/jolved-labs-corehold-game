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

    // Difficulty (GDD §2.3) lives in Corehold.Core, on GameManager.cs.
    //
    // There was a second, identical copy here, and nothing ever referenced it —
    // its only mention in this whole assembly was its own declaration. What it
    // did do was collide: any file importing both Corehold.Core and
    // Corehold.Data and naming Difficulty got CS0104, and the codebase had
    // already grown three separate workarounds for that (an alias in
    // CampaignWelcome, fully-qualified names in HUDController and
    // Wave9MemoryProbe) before a fourth file finally just failed to compile.
    // Deleting the unused copy is what actually fixes it.
}
