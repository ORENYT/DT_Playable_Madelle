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

        /// <summary>Detail log line marked with [dbg] (printed only when "Show extended output" is on).</summary>
        internal static void V(string message)
        {
            Plugin.Print("[dbg] " + message);
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
                // Hand the Mastermind role to another real player; if there is none (only dummies besides the host),
                // to a dummy. SetMasterMind re-enters Pick through the patch with the new Mastermind.
                if (special != null && special == mastermind)
                {
                    var others = room.Players.Where(p => p != special && !p.IsDummy && !p.IsSpectator).ToList();
                    if (others.Count == 0)
                        others = room.Players.Where(p => p != special && !p.IsSpectator).ToList();
                    if (others.Count > 0)
                    {
                        SPlayer newMastermind = others[new Random().Next(others.Count)];
                        newMastermind.Color = EPlayerColor.Dark;
                        Plugin.Print($"Host was the Mastermind: '{newMastermind.Name}' becomes the Mastermind, the host stays the accomplice");
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
                Plugin.Print($"Accomplice = pid {SpecialId} '{special.Name}' (host={special == room.Host}), mastermind = '{mastermind?.Name}'");
            }
            else
            {
                V($"No accomplice this round (players={room.Players.Count}, " +
                  $"real={room.Players.Count(p => !p.IsDummy && !p.IsSpectator)}, min={Plugin.MinPlayers})");
            }
        }

        /// <summary>
        /// The minimap draws every other player only for non-White players, and paints a pin black (and the name pink)
        /// only for ids the client knows as "black" (KnownBlackIds). The client learns them solely from S_NOTIFY_BLACK,
        /// which the game sends only Mastermind &lt;-&gt; Black. So the Mastermind and the accomplice have to be told about
        /// each other explicitly. Note: a Dark client also shows the game's "has become the Shadow" notice for such a packet.
        /// </summary>
        private static void NotifyKnownBlack(SPlayer receiver, SPlayer teammate)
        {
            if (receiver == null || teammate == null || receiver.IsDummy || receiver.Session == null) return;
            receiver.Session.Send(new S_NOTIFY_BLACK { PlayerId = teammate.PublicInfo.PlayerId, ByHand = false });
            Plugin.Print($"'{receiver.Name}' now knows '{teammate.Name}' (pid {teammate.PublicInfo.PlayerId}) as a teammate");
        }

        /// <summary>A player just became Black (took the knife): the accomplice learns about them too.</summary>
        internal static void OnBecameBlack(SPlayer black)
        {
            if (SpecialId == None) return;
            var room = GameRoom.Instance;
            if (room == null) return;

            SPlayer accomplice = room.Players.FirstOrDefault(IsSpecial);
            if (accomplice == null || accomplice == black || !accomplice.IsAlive) return;
            NotifyKnownBlack(accomplice, black);
        }

        /// <summary>
        /// The game shows the weapon (knife) and fusebox sabotage pins on the minimap only to the Mastermind
        /// (S_SABOTAGE_MISSION). Mirror the same packet to the accomplice so they see the same pins.
        /// </summary>
        internal static void MirrorMissionPin(GameRoom room, ESchoolMission type, int deviceId, PosInfo pos, bool isAdd)
        {
            if (SpecialId == None || pos == null) return;

            SPlayer accomplice = room.Players.FirstOrDefault(IsSpecial);
            if (accomplice == null || accomplice == room.MasterMind || accomplice.IsDummy || accomplice.Session == null || !accomplice.IsAlive) return;

            accomplice.Session.Send(new S_SABOTAGE_MISSION { MissionType = type, DeviceId = deviceId, Pos = pos.Clone(), IsAdd = isAdd });
            Plugin.Print($"{type} pin {(isAdd ? "added" : "removed")} for '{accomplice.Name}' (device {deviceId})");
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
            _formTargetId = 0;
            _formEndTick = 0;
            _cooldownEndTick = 0;
            _lastDeviceId = -1;
        }

        /// <summary>
        /// PlayerId a clue is recorded under (see Device.RecordLastUsingPlayer). Clues are stored by PlayerId only and the
        /// clients resolve name and picture from it when the clue is viewed, so while the accomplice is shapeshifted his
        /// clues are recorded under the player he imitates. Otherwise they would always point at Madeline.
        /// </summary>
        internal static int ClueOwnerId(SPlayer p)
        {
            int own = p.PublicInfo.PlayerId;
            if (_formActive && _formTargetId > 0 && IsSpecial(p))
            {
                V($"clue recorded under pid {_formTargetId} (imitated player) instead of pid {own}");
                return _formTargetId;
            }
            return own;
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
            Plugin.Print("Characters (number - name - character):");
            for (int i = 0; i < room.Players.Count; i++)
            {
                SPlayer p = room.Players[i];
                int id = p.PublicInfo.CharacterId;
                CharacterData cd = Managers.Data.CharacterDic.Values.FirstOrDefault(c => c.DataId == id);
                Plugin.Print($"  {i + 1} - {p.Name} - {(cd != null ? cd.Type.ToString() : "?")}");
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

            Plugin.Print("Round over: ending the shapeshift before the results screen");
            EndForm(special, auto: true);
        }

        /// <summary>Body found (Survive -> Detective): if the accomplice is shapeshifted, return to the base look right away.</summary>
        internal static void OnBodyFound(GameRoom room)
        {
            if (SpecialId == None || !_formActive) return;

            SPlayer special = room.Players.FirstOrDefault(IsSpecial);
            if (special == null) return;

            Plugin.Print("Body found: ending the shapeshift");
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

            // Right after the list is sent, the Mastermind and the accomplice learn about each other
            // (black pin on the minimap, pink name).
            SPlayer mastermind = room.MasterMind;
            if (mastermind != null && mastermind != special)
            {
                NotifyKnownBlack(special, mastermind);   // the accomplice learns who the Mastermind is
                NotifyKnownBlack(mastermind, special);   // the Mastermind learns who the accomplice is
            }
            else
            {
                V("teammate notification skipped (no separate Mastermind)");
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
                    Plugin.Print($"Nickname restored: '{real}'");
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
                Plugin.Print($"Freeze: no valid target (targetId={pkt.TargetId}), skipped");
                return;
            }
            if (target.BuffComponent.HasBuff(EBuffType.TheWorld))
            {
                Plugin.Print($"Freeze: '{target.Name}' is already frozen, skipped");
                return;
            }

            // The client picks the nearest target within Range * 224 with line of sight; here only a rough distance check.
            float maxDist = (sc.Data != null ? sc.Data.Range : 1f) * 224f * 1.5f;
            float dist = (float)Math.Sqrt(Util.CalculateDistanceSquared(owner.PublicInfo.Pos, target.PublicInfo.Pos));
            if (dist > maxDist)
            {
                Plugin.Print($"Freeze: '{target.Name}' is too far ({dist:F0} > {maxDist:F0}), skipped");
                return;
            }

            int seconds = Plugin.FreezeDuration.Value;
            int cooldown = Plugin.FreezeCooldown.Value;
            target.BuffComponent.AddBuff(EBuffType.TheWorld, seconds * 1000);
            room.BroadcastWorldSFX(ESoundType.TheWorldSfx, owner.PublicInfo.Pos);

            if (CoolSkillMethod != null) CoolSkillMethod.Invoke(sc, new object[] { cooldown });
            else Plugin.Log.LogWarning("Freeze: SkillComponent.CoolSkill not found, cooldown was not started");

            Plugin.Print($"Freeze: '{target.Name}' (pid {target.PublicInfo.PlayerId}) frozen for {seconds}s (dist {dist:F0}), cooldown {cooldown}s");
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
            Plugin.Print($"Accomplice starts with skin {cd.Type} (id {cd.DataId}), own pick was id {_ownCharacterId}");
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
                Plugin.Print($"!{number}: no such player, skipped");
                return;
            }

            SPlayer target = players[number - 1];
            V($"!{number}: target='{target.Name}' pid={target.PublicInfo.PlayerId} char={target.PublicInfo.CharacterId} " +
              $"dummy={target.IsDummy} spectator={target.IsSpectator} self={target == me}");
            // Dummies are a fine target: you can copy players who left, as well as test bots.
            if (target != me && target.IsSpectator)
            {
                Plugin.Print($"!{number}: target is a spectator, skipped");
                return;
            }

            // The nickname respawn only sends packets to OTHER clients, so the player's own state (Interact in a
            // chat device, Hide, Carry ...) does not matter. Only state transitions and host migration are blocked.
            if (room.IsMigrating || room.IsTransitioning)
            {
                Plugin.Print($"!{number}: blocked (migrating={room.IsMigrating}, transitioning={room.IsTransitioning})");
                return;
            }

            if (_ownCharacterId == 0) _ownCharacterId = me.PublicInfo.CharacterId;

            int now = TimeManager.Instance.SurviveTime;

            // Own number = return to the base look early.
            if (target == me)
            {
                if (!_formActive)
                {
                    Plugin.Print($"!{number}: own number, but not shapeshifted - skipped");
                    return;
                }
                EndForm(me, auto: false);
                return;
            }

            if (_formActive)
            {
                Plugin.Print($"!{number}: already shapeshifted ({Math.Max(0, _formEndTick - now)}s left) - skipped");
                return;
            }
            if (now < _cooldownEndTick)
            {
                Plugin.Print($"!{number}: on cooldown ({_cooldownEndTick - now}s left) - skipped");
                return;
            }

            StartForm(me, target, number);
        }

        // ---------- shapeshift: lasts Duration seconds, then back to the base look, then a cooldown ----------

        private static bool _formActive;
        private static int _formTargetId;      // PlayerId of the player the accomplice is currently imitating
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
            _formTargetId = target.PublicInfo.PlayerId;

            int duration = Plugin.TransformDuration.Value;
            _formEndTick = TimeManager.Instance.SurviveTime + duration;
            _formJob = TimeManager.Instance.PushSurvivalJob(duration, delegate { EndForm(me, auto: true); });

            Plugin.Print($"!{number}: shapeshifted, skin={me.PublicInfo.CharacterId} nick='{me.Name}', duration={duration}s");
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
            Plugin.Print($"[to all] {text} (terminal {device})");
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
            _formTargetId = 0;
            _formJob?.Kill();
            _formJob = null;

            var room = GameRoom.Instance;
            int baseSkin = BaseSkinId();
            V($"EndForm(auto={auto}): back to skin={baseSkin}, nick='{_realName}'");
            ApplyLook(room, me, baseSkin, _realName);

            int cooldown = Plugin.TransformCooldown.Value;
            _cooldownEndTick = TimeManager.Instance.SurviveTime + cooldown;
            Plugin.Print($"Shapeshift ended ({(auto ? "time is up" : "manual")}); cooldown {cooldown}s");
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
            Plugin.Print($"Rename pid={pid} '{oldName}' -> '{p.Name}': LEAVE+ADD sent to {sent} client(s), " +
                               $"SPAWN to {spawned}, skipped {skipped} dummy/no-session (sharedWith={p.SharedPlayers.Count})");
        }

        // ---------- private message ----------

        internal static void Tell(SPlayer p, string text)
        {
            Plugin.Print($"[to {p.Name}] {text}");
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
