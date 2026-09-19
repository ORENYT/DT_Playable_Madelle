using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Protocol;

namespace DarkAccomplice
{
    /// <summary>
    /// Host-side mod. At the start of a round, besides the Mastermind, one random player becomes a second Dark (the "accomplice").
    /// The accomplice:
    ///  - starts as Madeline (a skin regular players cannot pick);
    ///  - has a custom ability: a Louis-style target mark, but the target is frozen like Seol's TimeStop;
    ///  - can send "!N" in the normal terminal (chat device) chat to shapeshift into player N (skin and nickname).
    /// Everything works on the host only: other players need no mods.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.oreny.darkaccomplice";
        public const string PluginName = "DarkAccomplice";
        public const string PluginVersion = "1.0.0";

        // Fixed defaults (intentionally not configurable).
        internal const int MinPlayers = 3;                                    // minimum real players to pick an accomplice
        internal const ECharacterType StartSkin = ECharacterType.Madeline;    // skin the accomplice starts with and returns to

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> HostIsAccomplice;
        internal static ConfigEntry<int> FreezeDuration;
        internal static ConfigEntry<int> FreezeCooldown;
        internal static ConfigEntry<int> TransformDuration;
        internal static ConfigEntry<int> TransformCooldown;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true,
                "Enable the mod (only has an effect when you are the host)");

            HostIsAccomplice = Config.Bind("Debug", "Host Is Accomplice", false,
                "TEST: the host always becomes the accomplice (starts as Madeline). Handy for solo testing together with SoloStart");

            FreezeDuration = Config.Bind("Freeze Ability", "Duration Seconds", 10,
                new ConfigDescription("How long the target stays frozen", new AcceptableValueRange<int>(1, 60)));
            FreezeCooldown = Config.Bind("Freeze Ability", "Cooldown Seconds", 45,
                new ConfigDescription("Ability cooldown", new AcceptableValueRange<int>(1, 300)));

            TransformDuration = Config.Bind("Transform", "Duration Seconds", 30,
                new ConfigDescription("How long a shapeshift lasts before returning to the base look (Madeline and the real nickname)",
                    new AcceptableValueRange<int>(1, 600)));
            TransformCooldown = Config.Bind("Transform", "Cooldown Seconds", 15,
                new ConfigDescription("Pause before the next shapeshift; counted from the moment the base look returns (in-round time)",
                    new AcceptableValueRange<int>(0, 600)));

            try
            {
                Harmony.CreateAndPatchAll(typeof(Plugin).Assembly, PluginGuid);
                Logger.LogInfo($"{PluginName} {PluginVersion} loaded");
            }
            catch (Exception e)
            {
                Logger.LogError($"Patches failed to apply (did the game update?): {e}");
            }
        }
    }
}
