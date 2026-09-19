using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Protocol;
using Server.Game;
using CharacterData = Data.CharacterData;
using SPlayer = Server.Game.Player;

namespace DarkAccomplice
{
    /// <summary>Accomplice state and logic. Everything runs on the host, on the GameRoom job thread.</summary>
    internal static class Accomplice
    {
        internal const int None = -1;

        internal static int SpecialId = None;   // PlayerId of this round's accomplice
        private static string _realName;        // the accomplice's real nickname (to switch back to)
        private static int _ownCharacterId;     // the character the accomplice picked (fallback base skin)
        private static bool _nickChanged;

        // While true, AllocateSkill does not reassign the ability (a skin change must not reset the cooldown).
        internal static bool SuppressAllocate;

        internal static bool IsSpecial(SPlayer p)
        {
            return p != null && SpecialId != None && p.PublicInfo != null && p.PublicInfo.PlayerId == SpecialId;
        }

        /// <summary>Verbose log line (always on; marked with [dbg]).</summary>
        internal static void V(string message)
        {
            Plugin.Log.LogInfo("[dbg] " + message);
        }

        // ---------- picking the accomplice ----------

        internal static void Pick(GameRoom room, SPlayer mastermind)
        {
            Reset();
            if (!Plugin.Enabled.Value) return;

            SPlayer special = null;
            if (Plugin.HostIsAccomplice.Value)
            {
                special = room.Host;

                // The host cannot be both the Mastermind and the accomplice (that would leave only one Dark).
                // If another real player exists, hand the Mastermind role to them; SetMasterMind re-enters Pick
                // through the patch with the new Mastermind.
                if (special != null && special == mastermind)
                {
                    var others = room.Players.Where(p => p != special && !p.IsDummy && !p.IsSpectator).ToList();
                    if (others.Count > 0)
                    {
                        SPlayer newMastermind = others[new Random().Next(others.Count)];
                        newMastermind.Color = EPlayerColor.Dark;
                        Plugin.Log.LogInfo($"Host was the Mastermind: '{newMastermind.Name}' becomes the Mastermind, the host stays the accomplice");
                        room.SetMasterMind(newMastermind);
                        return;
                    }
                }
            }
            else if (room.Players.Count(p => !p.IsDummy && !p.IsSpectator) >= Plugin.MinPlayers)
            {
                var candidates = room.Players
                    .Where(p => p != mastermind && !p.IsDummy && !p.IsSpectator)
                    .ToList();
                if (candidates.Count > 0) special = candidates[new Random().Next(candidates.Count)];
            }

            if (special != null)
            {
                SpecialId = special.PublicInfo.PlayerId;
                _realName = special.Name;
                special.Color = EPlayerColor.Dark;
                Plugin.Log.LogInfo($"Accomplice = pid {SpecialId} '{special.Name}' (host={special == room.Host}), mastermind = '{mastermind?.Name}'");
            }
            else
            {
                V($"No accomplice this round (players={room.Players.Count}, " +
                  $"real={room.Players.Count(p => !p.IsDummy && !p.IsSpectator)}, min={Plugin.MinPlayers})");
            }
        }

        internal static void Reset()
        {
            _helpSent = false;
            SpecialId = None;
            _realName = null;
            _ownCharacterId = 0;
            _nickChanged = false;
            SuppressAllocate = false;

            _formJob?.Kill();
            _formJob = null;
            _formActive = false;
            _formEndTick = 0;
            _cooldownEndTick = 0;
            _lastDeviceId = -1;
        }

        // ---------- round flow hooks ----------

        /// <summary>
        /// Character selection is over (right before the round starts): put the accomplice into the start skin
        /// and print the "number - name - character" table.
        /// </summary>
        internal static void OnPickFinished(GameRoom room)
        {
            if (!Plugin.Enabled.Value) return;
            ApplyStartSkin(room);
            LogCharacterTable(room);
        }

        /// <summary>Console table "number - name - character" (the number is the one used in the "!N" command).</summary>
        private static void LogCharacterTable(GameRoom room)
        {
            Plugin.Log.LogInfo("Characters (number - name - character):");
            for (int i = 0; i < room.Players.Count; i++)
            {
                SPlayer p = room.Players[i];
                int id = p.PublicInfo.CharacterId;
                CharacterData cd = Managers.Data.CharacterDic.Values.FirstOrDefault(c => c.DataId == id);
                Plugin.Log.LogInfo($"  {i + 1} - {p.Name} - {(cd != null ? cd.Type.ToString() : "?")}");
            }
        }

        /// <summary>
        /// Round over (win or loss, switching to TotalResult): if the accomplice is still shapeshifted,
        /// return to the base look so the results screen shows Madeline with the real nickname.
        /// </summary>
        internal static void OnRoundEnd(GameRoom room)
        {
            if (SpecialId == None || !_formActive) return;

            SPlayer special = room.Players.FirstOrDefault(IsSpecial);
            if (special == null) return;

            Plugin.Log.LogInfo("Round over: ending the shapeshift before the results screen");
            EndForm(special, auto: true);
        }

        /// <summary>Body found (Survive -> Detective): if the accomplice is shapeshifted, return to the base look right away.</summary>
        internal static void OnBodyFound(GameRoom room)
        {
            if (SpecialId == None || !_formActive) return;

            SPlayer special = room.Players.FirstOrDefault(IsSpecial);
            if (special == null) return;

            Plugin.Log.LogInfo("Body found: ending the shapeshift");
            EndForm(special, auto: true);
        }

        private static bool _helpSent;

        /// <summary>
        /// Called on every Survive start (also after a trial).
        /// 1) EndSurvival wipes ALL in-round timers, so the shapeshift timer is re-armed for the remaining time.
        /// 2) Once per round, 5 seconds after the start, the player list is sent to the accomplice's terminal.
        /// </summary>
        internal static void OnSurviveStart(GameRoom room)
        {
            if (SpecialId == None) return;

            SPlayer special = room.Players.FirstOrDefault(IsSpecial);
            if (special == null) return;

            if (_formActive)
            {
                int remaining = _formEndTick - TimeManager.Instance.SurviveTime;
                _formJob?.Kill();
                _formJob = null;
                if (remaining <= 0)
                {
                    V("Survive resumed: shapeshift time is already up, ending now");
                    EndForm(special, auto: true);
                }
                else
                {
                    V($"Survive resumed: shapeshift timer re-armed for {remaining}s");
                    _formJob = TimeManager.Instance.PushSurvivalJob(remaining, delegate { EndForm(special, auto: true); });
                }
            }

            if (!_helpSent)
            {
                _helpSent = true;
                room.PushAfter(5000, delegate { AutoHelp(room); });
                V("player list scheduled in 5s");
            }
        }

        private static void AutoHelp(GameRoom room)
        {
            SPlayer special = room.Players.FirstOrDefault(IsSpecial);
            if (special == null || room.State != EGameState.Survive)
            {
                V($"player list skipped (special={(special != null)}, state={room.State})");
                return;
            }

            // Into the terminal (chat device window): the client stores the message in its NormalLog
            // and shows it when any terminal is opened.
            Server.Game.ChatDevice terminal = Server.Game.DeviceManager.Instance.Objects.OfType<Server.Game.ChatDevice>().FirstOrDefault();
            int saved = _replyDeviceId;
            _replyDeviceId = terminal != null ? terminal.ID : -1;
            try
            {
                if (terminal == null) V("player list: no chat device found, message goes to the plain chat");
                Tell(special, PlayerList(room) + ". To shapeshift send !1 (or any other number) in the terminal");
            }
            finally
            {
                _replyDeviceId = saved;
            }
        }

        /// <summary>Back in the lobby: reset the accomplice and restore the real nickname. The Madeline skin stays.</summary>
        internal static void OnLobby(GameRoom room)
        {
            if (SpecialId == None) return;

            int pid = SpecialId;
            string real = _realName;
            bool restore = _nickChanged;
            Reset();

            if (!restore || string.IsNullOrEmpty(real)) return;
            // Let the lobby respawn the players first, then update the nickname.
            room.PushAfter(1500, delegate
            {
                SPlayer p = room.Players.FirstOrDefault(x => x.PublicInfo.PlayerId == pid);
                if (p != null && !p.IsDummy && p.Name != real)
                {
                    Rename(room, p, real);
                    Plugin.Log.LogInfo($"Nickname restored: '{real}'");
                }
            });
        }

        // ---------- ability: freeze the target ----------

        /// <summary>
        /// The ability rides on a Telekinesis "carrier": the client draws the Louis target mark and sends the TargetId,
        /// while the effect is replaced on the host (see UseFreeze).
        /// </summary>
        internal static CharacterData WithAbility(CharacterData original)
        {
            var src = Managers.Data.CharacterDic.Values.FirstOrDefault(c => c.Skill == ESkillType.Telekinesis);
            if (src == null || original == null) return original;
            return new CharacterData
            {
                DataId = original.DataId,
                Type = original.Type,
                Name = original.Name,
                SkeletonPrefabName = original.SkeletonPrefabName,
                Skill = src.Skill
            };
        }

        private static readonly System.Reflection.MethodInfo CoolSkillMethod =
            AccessTools.Method(typeof(SkillComponent), "CoolSkill", new[] { typeof(int) });

        /// <summary>
        /// Instead of Telekinesis: the target (picked by the client with the Louis mark) is frozen for the configured time
        /// by the TheWorld buff, exactly like Seol's TimeStop (on clients the target turns grey and motionless, controls locked).
        /// The cooldown is the configured value.
        /// </summary>
        internal static void UseFreeze(SkillComponent sc, C_USE_SKILL pkt)
        {
            SPlayer owner = sc.Owner;
            var room = GameRoom.Instance;
            V($"freeze: request targetId={pkt.TargetId}, canUse={owner.CanUseSkill}");
            if (!owner.CanUseSkill) return;

            SPlayer target = room.AlivePlayers.FirstOrDefault(p => p.PublicInfo.PlayerId == pkt.TargetId);
            if (target == null || target == owner)
            {
                Plugin.Log.LogInfo($"Freeze: no valid target (targetId={pkt.TargetId}), skipped");
                return;
            }
            if (target.BuffComponent.HasBuff(EBuffType.TheWorld))
            {
                Plugin.Log.LogInfo($"Freeze: '{target.Name}' is already frozen, skipped");
                return;
            }

            // The client picks the nearest target within Range * 224 with line of sight; here only a rough distance check.
            float maxDist = (sc.Data != null ? sc.Data.Range : 1f) * 224f * 1.5f;
            float dist = (float)Math.Sqrt(Util.CalculateDistanceSquared(owner.PublicInfo.Pos, target.PublicInfo.Pos));
            if (dist > maxDist)
            {
                Plugin.Log.LogInfo($"Freeze: '{target.Name}' is too far ({dist:F0} > {maxDist:F0}), skipped");
                return;
            }

            int seconds = Plugin.FreezeDuration.Value;
            int cooldown = Plugin.FreezeCooldown.Value;
            target.BuffComponent.AddBuff(EBuffType.TheWorld, seconds * 1000);
            room.BroadcastWorldSFX(ESoundType.TheWorldSfx, owner.PublicInfo.Pos);

            if (CoolSkillMethod != null) CoolSkillMethod.Invoke(sc, new object[] { cooldown });
            else Plugin.Log.LogWarning("Freeze: SkillComponent.CoolSkill not found, cooldown was not started");

            Plugin.Log.LogInfo($"Freeze: '{target.Name}' (pid {target.PublicInfo.PlayerId}) frozen for {seconds}s (dist {dist:F0}), cooldown {cooldown}s");
        }

        // ---------- start skin ----------

        private static CharacterData StartSkinData()
        {
            return Managers.Data.CharacterDic.Values.FirstOrDefault(c => c.Type == Plugin.StartSkin);
        }

        /// <summary>
        /// Before the round starts (character selection is over) the accomplice is forced into the start skin
        /// (Madeline: regular players cannot pick her, the game excludes Type == 0 from the choices).
        /// </summary>
        internal static void ApplyStartSkin(GameRoom room)
        {
            if (SpecialId == None) return;

            SPlayer special = room.Players.FirstOrDefault(IsSpecial);
            if (special == null) return;

            CharacterData cd = StartSkinData();
            if (cd == null)
            {
                Plugin.Log.LogWarning($"Start skin {Plugin.StartSkin} not found in CharacterDic");
                return;
            }
            if (special.PublicInfo.CharacterId == cd.DataId) return;

            _ownCharacterId = special.PublicInfo.CharacterId; // remember the accomplice's own pick
            SetSkin(special, cd.DataId);
            Plugin.Log.LogInfo($"Accomplice starts with skin {cd.Type} (id {cd.DataId}), own pick was id {_ownCharacterId}");
        }

        /// <summary>
        /// The CharacterId setter changes the look for everyone and calls AllocateSkill.
        /// The ability and its cooldown must stay untouched (see the AllocateSkill patch).
        /// </summary>
        private static void SetSkin(SPlayer p, int dataId)
        {
            int before = p.PublicInfo.CharacterId;
            var skillBefore = p.SkillComponent.Data?.Type;
            SuppressAllocate = p.SkillComponent.Data != null;
            try { p.CharacterId = dataId; }
            finally { SuppressAllocate = false; }
            V($"SetSkin {before} -> {dataId} (now {p.PublicInfo.CharacterId}); skill before={skillBefore} after={p.SkillComponent.Data?.Type}");
        }

        // ---------- the "!N" command ----------

        // Where to reply: the client shows the normal chat only in Lobby and Trial, and during a round only
        // chat device rows. So the reply goes to the same terminal the command came from.
        private static int _replyDeviceId = -1;

        /// <summary>Returns true if the message was an accomplice command (it must not be relayed to the chat).</summary>
        internal static bool TryHandle(SPlayer sender, string text, int deviceId = -1, bool isSecret = false)
        {
            if (!IsSpecial(sender) || string.IsNullOrEmpty(text) || text[0] != '!') return false;

            string body = text.Substring(1).Trim();
            V($"chat from accomplice: '{text}' device={deviceId} secret={isSecret} roomState={GameRoom.Instance.State} " +
              $"playerState={sender.State} alive={sender.IsAlive} migrating={GameRoom.Instance.IsMigrating} transitioning={GameRoom.Instance.IsTransitioning}");
            if (body.Length == 0) return false;

            bool isHelp = body.Equals("help", StringComparison.OrdinalIgnoreCase);
            bool isNumber = body.All(c => c >= '0' && c <= '9');
            if (!isHelp && !isNumber) return false; // an ordinary message, not our command

            // "!help" is executed only by the plugin itself (once, automatically, 5 s after Survive starts).
            // Nobody can call it manually: the message is silently swallowed.
            if (isHelp)
            {
                V("manual !help is disabled (the list is sent only by the plugin), message swallowed");
                return true;
            }

            // Commands work only in the normal (non-secret) terminal chat (chat device).
            // Anywhere else the message is silently swallowed so that others do not see "!3".
            if (deviceId <= 0 || isSecret)
            {
                V($"command '{text}' ignored: works only in the normal terminal chat (device={deviceId}, secret={isSecret})");
                return true;
            }

            _replyDeviceId = deviceId;
            _lastDeviceId = deviceId;
            try
            {
                // An invalid number is silently ignored (the message is swallowed, nothing happens).
                if (!int.TryParse(body, out int number))
                {
                    V($"'{body}' is not a valid number, skipped");
                    return true;
                }
                Impersonate(sender, number);
                return true;
            }
            finally
            {
                _replyDeviceId = -1;
            }
        }

        private static void Impersonate(SPlayer me, int number)
        {
            var room = GameRoom.Instance;
            var players = room.Players;
            V($"!{number}: players={players.Count}, list=[{PlayerList(room)}]");
            if (number < 1 || number > players.Count)
            {
                Plugin.Log.LogInfo($"!{number}: no such player, skipped");
                return;
            }

            SPlayer target = players[number - 1];
            V($"!{number}: target='{target.Name}' pid={target.PublicInfo.PlayerId} char={target.PublicInfo.CharacterId} " +
              $"dummy={target.IsDummy} spectator={target.IsSpectator} self={target == me}");
            // Dummies are a fine target: you can copy players who left, as well as test bots.
            if (target != me && target.IsSpectator)
            {
                Plugin.Log.LogInfo($"!{number}: target is a spectator, skipped");
                return;
            }

            // The nickname respawn only sends packets to OTHER clients, so the player's own state (Interact in a
            // chat device, Hide, Carry ...) does not matter. Only state transitions and host migration are blocked.
            if (room.IsMigrating || room.IsTransitioning)
            {
                Plugin.Log.LogInfo($"!{number}: blocked (migrating={room.IsMigrating}, transitioning={room.IsTransitioning})");
                return;
            }

            if (_ownCharacterId == 0) _ownCharacterId = me.PublicInfo.CharacterId;

            int now = TimeManager.Instance.SurviveTime;

            // Own number = return to the base look early.
            if (target == me)
            {
                if (!_formActive)
                {
                    Plugin.Log.LogInfo($"!{number}: own number, but not shapeshifted - skipped");
                    return;
                }
                EndForm(me, auto: false);
                return;
            }

            if (_formActive)
            {
                Plugin.Log.LogInfo($"!{number}: already shapeshifted ({Math.Max(0, _formEndTick - now)}s left) - skipped");
                return;
            }
            if (now < _cooldownEndTick)
            {
                Plugin.Log.LogInfo($"!{number}: on cooldown ({_cooldownEndTick - now}s left) - skipped");
                return;
            }

            StartForm(me, target, number);
        }

        // ---------- shapeshift: lasts Duration seconds, then back to the base look, then a cooldown ----------

        private static bool _formActive;
        private static int _formEndTick;
        private static int _cooldownEndTick;   // in TimeManager.SurviveTime ticks (runs only during Survive, like skill cooldowns)
        private static JobElem _formJob;
        private static int _lastDeviceId = -1; // last terminal the accomplice used

        private static void StartForm(SPlayer me, SPlayer target, int number)
        {
            var room = GameRoom.Instance;
            int skin = target.PublicInfo.CharacterId;
            string nick = target.Name;
            V($"!{number}: SHAPESHIFT skin={skin} (current {me.PublicInfo.CharacterId}), nick='{nick}' (current '{me.Name}', real '{_realName}')");

            ApplyLook(room, me, skin, nick);
            _formActive = true;

            int duration = Plugin.TransformDuration.Value;
            _formEndTick = TimeManager.Instance.SurviveTime + duration;
            _formJob = TimeManager.Instance.PushSurvivalJob(duration, delegate { EndForm(me, auto: true); });

            Plugin.Log.LogInfo($"!{number}: shapeshifted, skin={me.PublicInfo.CharacterId} nick='{me.Name}', duration={duration}s");
            Announce(me, "shapeshifted");
        }

        /// <summary>
        /// Writes a message into the terminal for EVERY alive player (not just the accomplice), the same way the game
        /// relays chat device messages: sent to all alive real players and recorded in the device chat log,
        /// so it is also replayed during the investigation.
        /// </summary>
        private static void Announce(SPlayer me, string text)
        {
            int device = _replyDeviceId > 0 ? _replyDeviceId : _lastDeviceId;
            Plugin.Log.LogInfo($"[to all] {text} (terminal {device})");
            if (device <= 0)
            {
                Tell(me, text); // should not happen: commands only come from a terminal
                return;
            }

            var msg = new S_CHAT_MESSAGE
            {
                Type = EChatType.DeviceChat,
                Text = text,
                PlayerId = 0,
                DeviceId = device,
                Time = TimeManager.Instance.SurviveTime
            };
            Server.Game.Replicator.AliveReal(msg);
            GameRoom.Instance.RecordDeviceChat(msg);
        }

        private static void EndForm(SPlayer me, bool auto)
        {
            if (!_formActive) return;
            _formActive = false;
            _formJob?.Kill();
            _formJob = null;

            var room = GameRoom.Instance;
            int baseSkin = BaseSkinId();
            V($"EndForm(auto={auto}): back to skin={baseSkin}, nick='{_realName}'");
            ApplyLook(room, me, baseSkin, _realName);

            int cooldown = Plugin.TransformCooldown.Value;
            _cooldownEndTick = TimeManager.Instance.SurviveTime + cooldown;
            Plugin.Log.LogInfo($"Shapeshift ended ({(auto ? "time is up" : "manual")}); cooldown {cooldown}s");
            // Nothing is written to the terminal here: it only ever gets the player list and "shapeshifted".
        }

        private static int BaseSkinId()
        {
            CharacterData cd = StartSkinData();
            return cd != null ? cd.DataId : _ownCharacterId;
        }

        /// <summary>Skin first (it is broadcast to everyone by the game), then the nickname: the respawn for other clients already carries the new skin.</summary>
        private static void ApplyLook(GameRoom room, SPlayer me, int skin, string nick)
        {
            if (skin > 0 && skin != me.PublicInfo.CharacterId)
            {
                SetSkin(me, skin);
            }
            else
            {
                V($"skin unchanged (skin={skin})");
            }

            if (!string.IsNullOrEmpty(nick) && nick != me.Name)
            {
                Rename(room, me, nick);
            }
            else
            {
                V("nick unchanged");
            }
            _nickChanged = !string.Equals(me.Name, _realName, StringComparison.Ordinal);
        }

        private static string PlayerList(GameRoom room)
        {
            var parts = new List<string>();
            for (int i = 0; i < room.Players.Count; i++)
                parts.Add($"{i + 1}:{room.Players[i].Name}");
            return "Players: " + string.Join(", ", parts);
        }

        // ---------- nickname change (respawn for the other clients) ----------

        /// <summary>
        /// Clients remember a name once, when the player object is created, and there is no "rename" packet.
        /// So for every OTHER client the player object is recreated:
        ///   S_LEAVE_GAME -> S_ADD_PLAYER (new name) -> S_SPAWN (only to those who currently see the player).
        /// S_LEAVE_GAME must never be sent to the player himself: his client would leave to the lobby.
        /// </summary>
        internal static void Rename(GameRoom room, SPlayer p, string newName)
        {
            string oldName = p.Name;
            var setter = AccessTools.PropertySetter(typeof(SPlayer), nameof(SPlayer.Name));
            if (setter == null) Plugin.Log.LogWarning("Rename: Player.Name setter not found, the server-side name will not change");
            setter?.Invoke(p, new object[] { newName });

            int pid = p.PublicInfo.PlayerId;
            int sent = 0, spawned = 0, skipped = 0;
            foreach (SPlayer viewer in room.Players.ToList())
            {
                if (viewer == p || viewer.IsDummy || viewer.Session == null)
                {
                    if (viewer != p) skipped++;
                    continue;
                }

                viewer.Session.Send(new S_LEAVE_GAME { PlayerId = pid });
                viewer.Session.Send(new S_ADD_PLAYER
                {
                    PlayerId = pid,
                    Name = newName,
                    AccountId = p.AccountID ?? "",
                    CharacterId = p.PublicInfo.CharacterId
                });
                sent++;
                if (p.SharedPlayers.Contains(viewer))
                {
                    viewer.Session.Send(new S_SPAWN { Player = p.PublicInfo.Clone() });
                    spawned++;
                }
            }
            room.MarkRosterDirty();
            Plugin.Log.LogInfo($"Rename pid={pid} '{oldName}' -> '{p.Name}': LEAVE+ADD sent to {sent} client(s), " +
                               $"SPAWN to {spawned}, skipped {skipped} dummy/no-session (sharedWith={p.SharedPlayers.Count})");
        }

        // ---------- private message ----------

        internal static void Tell(SPlayer p, string text)
        {
            Plugin.Log.LogInfo($"[to {p.Name}] {text}");
            try
            {
                if (_replyDeviceId > 0)
                {
                    // Reply into the chat device window the player is typing in (sent only to him).
                    p.Session?.Send(new S_CHAT_MESSAGE
                    {
                        Type = EChatType.DeviceChat,
                        Text = text,
                        PlayerId = 0,
                        DeviceId = _replyDeviceId,
                        Time = TimeManager.Instance.SurviveTime
                    });
                }
                else
                {
                    // Normal chat: the client only shows it in Lobby and Trial.
                    p.Session?.Send(new S_CHAT_MESSAGE
                    {
                        Type = EChatType.NormalChat,
                        Text = text,
                        PlayerId = p.PublicInfo.PlayerId,
                        IsDead = !p.IsAlive
                    });
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Tell failed: {e.Message}");
            }
        }
    }
}
