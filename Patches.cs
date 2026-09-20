using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Protocol;
using Server.Game;
using CharacterData = Data.CharacterData;
using SPlayer = Server.Game.Player;

namespace DarkAccomplice
{
    [HarmonyPatch]
    internal static class Patches
    {
        // 1) Pick the accomplice right after the game assigns the Mastermind (GameRoom.StartPick).
        //    SetMasterMind is also called on host migration, hence the state check.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.SetMasterMind))]
        private static void AfterSetMasterMind(GameRoom __instance, SPlayer player)
        {
            if (__instance.State != EGameState.PickCharacter || player == null) return;
            try { Accomplice.Pick(__instance, player); }
            catch (Exception e) { Plugin.Log.LogError($"Pick failed: {e}"); }
        }

        // 2) State transitions that need action just before they happen.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.ChangeGameState), new[] { typeof(EGameState) })]
        private static void BeforeChangeGameState(GameRoom __instance, EGameState state)
        {
            // Round over (win or loss): be Madeline with the real nickname by the results screen.
            if (state == EGameState.TotalResult)
            {
                try { Accomplice.OnRoundEnd(__instance); }
                catch (Exception e) { Plugin.Log.LogError($"OnRoundEnd failed: {e}"); }
                return;
            }

            // Body found (Survive -> Detective): end the shapeshift.
            if (state == EGameState.Detective && __instance.State == EGameState.Survive)
            {
                try { Accomplice.OnBodyFound(__instance); }
                catch (Exception e) { Plugin.Log.LogError($"OnBodyFound failed: {e}"); }
                return;
            }

            // Character selection is over (PickCharacter -> Survive): apply the start skin.
            if (state != EGameState.Survive || __instance.State != EGameState.PickCharacter) return;
            try { Accomplice.OnPickFinished(__instance); }
            catch (Exception e) { Plugin.Log.LogError($"OnPickFinished failed: {e}"); }
        }

        // 3) Back in the lobby: reset the accomplice and restore the real nickname.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.ChangeGameState), new[] { typeof(EGameState) })]
        private static void AfterChangeGameState(GameRoom __instance, EGameState state)
        {
            if (state != EGameState.Lobby) return;
            try { Accomplice.OnLobby(__instance); }
            catch (Exception e) { Plugin.Log.LogError($"OnLobby failed: {e}"); }
        }

        // 4) Ability: the game gives a skill by CharacterData.Skill. For the accomplice we hand out Telekinesis
        //    (the Louis target mark) as a carrier; the actual effect is replaced in UseActiveSkill below.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SkillComponent), nameof(SkillComponent.AllocateSkill))]
        private static bool BeforeAllocateSkill(SkillComponent __instance, ref CharacterData data)
        {
            if (!Accomplice.IsSpecial(__instance.Owner)) return true;
            if (Accomplice.SuppressAllocate)
            {
                Accomplice.V("AllocateSkill skipped (skin change: ability and cooldown are kept)");
                return false;
            }
            var original = data?.Skill;
            data = Accomplice.WithAbility(data);
            Accomplice.V($"AllocateSkill: skill {original} -> {data?.Skill} for pid {__instance.Owner.PublicInfo.PlayerId}");
            return true;
        }

        // 5) Ability execution: instead of the original Telekinesis the host freezes the target like Seol does.
        //    The client needs nothing special: it only sends the TargetId picked by the target mark.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SkillComponent), "UseActiveSkill")]
        private static bool BeforeUseActiveSkill(SkillComponent __instance, C_USE_SKILL pkt)
        {
            if (!Accomplice.IsSpecial(__instance.Owner)) return true;
            if (__instance.Data == null || __instance.Data.Type != ESkillType.Telekinesis) return true;

            try { Accomplice.UseFreeze(__instance, pkt); }
            catch (Exception e) { Plugin.Log.LogError($"UseFreeze failed: {e}"); }
            return false; // skip the original Telekinesis
        }

        // 6) Commands. They only work in the normal (non-secret) terminal chat (chat device). The normal chat
        //    (lobby / trial / results) and the secret chat also intercept them, but only swallow the message
        //    so that nobody else sees "!3".
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HostPacketHandler), "RelayNormalChat")]
        private static bool BeforeNormalChat(SPlayer sender, string text)
        {
            return !SafeHandle(sender, text, -1, false);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(HostPacketHandler), "RelayDeviceChat")]
        private static bool BeforeDeviceChat(SPlayer sender, int deviceId, string text, bool isSecret)
        {
            return !SafeHandle(sender, text, deviceId, isSecret);
        }

        // 7) Survive started (also after a trial): re-arm the shapeshift timer, send the player list to the terminal after 5 s.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        private static void AfterStartSurvive(GameRoom __instance)
        {
            try { Accomplice.OnSurviveStart(__instance); }
            catch (Exception e) { Plugin.Log.LogError($"OnSurviveStart failed: {e}"); }
        }

        // 9) Kill limit: the game gives Black 1 kill in rounds with fewer than 6 players and 2 (double kill) with 6 or more.
        //    All users (weapon pickup, the limit sent to the client, the round log) read this getter, so overriding it is enough.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.BlackKillLimit), MethodType.Getter)]
        private static void AfterBlackKillLimit(ref int __result)
        {
            if (!Plugin.Enabled.Value || !Plugin.ForceSingleKill.Value || __result == 1) return;
            Plugin.Print($"Kill limit forced to 1 (the game wanted {__result})");
            __result = 1;
        }

        // 8) Clues: every device records who used it through Device.RecordLastUsingPlayer, keyed by PlayerId.
        //    Replace "player.PublicInfo.PlayerId" with our lookup so a shapeshifted accomplice frames the imitated player.
        private static readonly MethodInfo GetPublicInfo = AccessTools.PropertyGetter(typeof(SPlayer), "PublicInfo");
        private static readonly MethodInfo GetPlayerId = AccessTools.PropertyGetter(typeof(PublicPlayerInfo), "PlayerId");
        private static readonly MethodInfo ClueOwner = AccessTools.Method(typeof(Accomplice), nameof(Accomplice.ClueOwnerId));

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(Server.Game.Device), "RecordLastUsingPlayer")]
        private static IEnumerable<CodeInstruction> RecordClueTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var result = new CodeMatcher(instructions)
                .MatchStartForward(new CodeMatch(OpCodes.Callvirt, GetPublicInfo), new CodeMatch(OpCodes.Callvirt, GetPlayerId))
                .ThrowIfInvalid("Device.RecordLastUsingPlayer: 'player.PublicInfo.PlayerId' not found")
                .Set(OpCodes.Call, ClueOwner)   // Player -> int
                .Advance(1)
                .RemoveInstruction()            // drop the now redundant get_PlayerId call
                .InstructionEnumeration();
            Plugin.Print("Device.RecordLastUsingPlayer: patched (shapeshifted clues)");
            return result;
        }

        private static bool SafeHandle(SPlayer sender, string text, int deviceId, bool isSecret)
        {
            try { return Accomplice.TryHandle(sender, text, deviceId, isSecret); }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Command failed: {e}");
                return false;
            }
        }
    }
}
