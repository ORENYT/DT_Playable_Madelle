using System;
using System.Collections.Generic;
using System.Linq;
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
            // Leaving the trial: the accomplice's real color goes back to Dark.
            if (__instance.State == EGameState.Trial && state != EGameState.Trial)
            {
                try { Accomplice.OnTrialExit(__instance); }
                catch (Exception e) { Plugin.Log.LogError($"OnTrialExit failed: {e}"); }
            }

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

        // 10) A player became Black: tell the accomplice (the game only tells the Mastermind and that Black to each other).
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SPlayer), nameof(SPlayer.Color), MethodType.Setter)]
        private static void AfterColorSet(SPlayer __instance, EPlayerColor value)
        {
            if (value != EPlayerColor.Black) return;
            try { Accomplice.OnBecameBlack(__instance); }
            catch (Exception e) { Plugin.Log.LogError($"OnBecameBlack failed: {e}"); }
        }

        // 14) The Mastermind handed the knife directly to someone (Player.HandWeapon) instead of it being picked up
        // from the armory - tell the accomplice. HandWeapon no-ops silently on several guards (wrong color, dead
        // target, target already has a weapon, ...), so success is checked afterwards instead of relying on the
        // call alone.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SPlayer), nameof(SPlayer.HandWeapon))]
        private static void AfterHandWeapon(SPlayer __instance, int targetId)
        {
            try
            {
                var room = GameRoom.Instance;
                SPlayer target = room?.Players.FirstOrDefault(p => p.PublicInfo.PlayerId == targetId);
                if (target == null || target.Color != EPlayerColor.Black || target.Weapon == null) return; // HandWeapon no-op'd

                Accomplice.OnKnifeHandedOff(__instance, target);
            }
            catch (Exception e) { Plugin.Log.LogError($"OnKnifeHandedOff failed: {e}"); }
        }

        // 16) The armory's "Dark" pickup (Armory.AcquireWeaponDark, EArmoryInteractType.SelectBlack) only checks
        // Color == Dark - meant for the Mastermind to grab the knife himself (e.g. to hand it off by hand), but
        // the accomplice is Dark too and would otherwise qualify. She can never use the knife anyway (Attack
        // requires Color == Black), so if she held it the weapon would just be stuck, unusable by anyone -
        // block her from taking it at all. The normal White-only AcquireWeapon already rejects her (she is
        // never White), so this is the only path that needs the guard.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Server.Game.Armory), "AcquireWeaponDark")]
        private static bool BeforeAcquireWeaponDark(SPlayer player)
        {
            if (!Accomplice.IsSpecial(player)) return true;
            Plugin.Print($"Armory pickup blocked for accomplice '{player.Name}' (she can never hold the knife)");
            return false;
        }

        // Reflection into DeviceManager internals the vanilla game keeps private - see BeforeScheduleFirstWeaponSpawn.
        private static readonly FieldInfo ArmoriesField = AccessTools.Field(typeof(Server.Game.DeviceManager), "_armories");
        private static readonly MethodInfo ArmoryIndexSetter = AccessTools.PropertySetter(typeof(Server.Game.DeviceManager), nameof(Server.Game.DeviceManager.ArmoryIndex));

        // 17) With "Weapon Spawn Delay" on, the game waits 30s before the first knife spawn and only picks (and
        // reveals) the armory at the very end of that wait, inside the private DeviceManager.SpawnFirstWeapon
        // (Util.Shuffle(_armories, ...); ArmoryIndex = 0; SpawnNextWeapon(isInit: true);). Do that exact same
        // shuffle right away instead, tell the real Mastermind the location immediately, then run the actual
        // spawn (SpawnNextWeapon, public) ourselves once the delay elapses - using the SAME already-shuffled
        // order (no second shuffle), so the location announced early is guaranteed to be the one that opens.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Server.Game.DeviceManager), nameof(Server.Game.DeviceManager.ScheduleFirstWeaponSpawn))]
        private static bool BeforeScheduleFirstWeaponSpawn(Server.Game.DeviceManager __instance, int delaySecond)
        {
            if (!Plugin.Enabled.Value || !Plugin.EarlyKnifeSpoiler.Value || delaySecond <= 0 || ArmoriesField == null || ArmoryIndexSetter == null) return true;

            try
            {
                var armories = ArmoriesField.GetValue(__instance) as List<Server.Game.Armory>;
                if (armories == null || armories.Count == 0) return true;

                Util.Shuffle(armories, armories.Count);
                ArmoryIndexSetter.Invoke(__instance, new object[] { 0 });

                SPlayer mastermind = GameRoom.Instance.MasterMind;
                if (mastermind != null && mastermind.IsAlive)
                {
                    GameRoom.Instance.SendSabotageMission(ESchoolMission.ScWeapon, armories[0].ID, armories[0].DeviceInfo.Pos, isAdd: true);
                    Plugin.Print($"Mastermind '{mastermind.Name}' told the first knife spawn location {delaySecond}s early");
                }

                GameRoom.Instance.PushAfter(delaySecond * 1000, delegate
                {
                    if (__instance.CurrentArmory == null) __instance.SpawnNextWeapon(isInit: true);
                });
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"BeforeScheduleFirstWeaponSpawn failed: {e}");
                return true; // fall back to vanilla behaviour on any failure
            }

            return false; // fully handled ourselves
        }

        // 11) Minimap pins: the game sends the knife and fusebox pins only to the Mastermind, mirror them to the accomplice.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.SendSabotageMission))]
        private static void AfterSendSabotageMission(GameRoom __instance, ESchoolMission type, int deviceId, PosInfo pos, bool isAdd)
        {
            try { Accomplice.MirrorMissionPin(__instance, type, deviceId, pos, isAdd); }
            catch (Exception e) { Plugin.Log.LogError($"MirrorMissionPin failed: {e}"); }
        }

        // 13) The accomplice must never be able to vote for a player (see the big comment in Accomplice.cs). The
        // game only blocks voting client-side for Color == Dark, and "!w"/"!r" give her a real White/Black color,
        // so the real block has to live here, on the host. SKIP stays allowed - only picking a name is blocked.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TrialManager), nameof(TrialManager.HandleEvent))]
        private static bool BeforeHandleTrialEvent(SPlayer player, Packet packet)
        {
            if (!Accomplice.IsSpecial(player)) return true;
            if (!(packet?.Pkt is C_HANDLE_TRIAL { Type: C_ETrialEventType.VotePlayer })) return true; // SKIP and everything else goes through

            Plugin.Print($"Vote from '{player.Name}' blocked (accomplice can never vote for a player)");
            return false;
        }

        // 12) The trial discussion has started: offer the accomplice a choice of side (white / red).
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TrialManager), nameof(TrialManager.State), MethodType.Setter)]
        private static void AfterTrialState(ETrialState value)
        {
            if (value != ETrialState.Discuss) return;
            var room = GameRoom.Instance;
            if (room == null || room.State != EGameState.Trial) return;
            try { Accomplice.OnTrialDiscuss(room); }
            catch (Exception e) { Plugin.Log.LogError($"OnTrialDiscuss failed: {e}"); }
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
