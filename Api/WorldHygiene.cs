namespace SideHustle
{
    /// <summary>
    /// Ready-made sets of the world hygiene flags on <see cref="GamemodeDescriptor"/>, so a World gamemode does not
    /// have to know which five booleans belong together. Each method sets the flags on the descriptor you pass and
    /// returns it, and only ever switches flags ON: anything you set yourself stays set. Since 3.0.0.
    /// </summary>
    public static class WorldHygiene
    {
        /// <summary>
        /// A clean scratch world: vanilla quests blocked (except <paramref name="allowedQuestTitles"/>), the intro
        /// skipped and saving blocked for the session. Fits most World gamemodes.
        /// </summary>
        public static GamemodeDescriptor CleanWorld(GamemodeDescriptor descriptor, params string[] allowedQuestTitles)
        {
            if (descriptor == null)
            {
                Core.Log?.Warning("[api] WorldHygiene.CleanWorld was called with a null descriptor - nothing set.");
                return null;
            }
            descriptor.BlockVanillaQuests = true;
            descriptor.SkipIntro = true;
            descriptor.BlockSaveDuringSession = true;
            if (allowedQuestTitles != null && allowedQuestTitles.Length > 0) descriptor.AllowedQuestTitles = allowedQuestTitles;
            return descriptor;
        }

        /// <summary>
        /// <see cref="CleanWorld"/> plus no NPC reactions to gunfire and no vanilla player death - for a gamemode
        /// whose players shoot and whose eliminations are its own (the set PropHunt uses).
        /// </summary>
        public static GamemodeDescriptor Arena(GamemodeDescriptor descriptor, params string[] allowedQuestTitles)
        {
            if (CleanWorld(descriptor, allowedQuestTitles) == null) return null;
            descriptor.SuppressNpcCombatReactions = true;
            descriptor.DisableVanillaPlayerDeath = true;
            return descriptor;
        }
    }
}
