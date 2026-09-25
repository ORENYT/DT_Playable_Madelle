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

        // Always on, not configurable: the accomplice never passively learns who her Dark teammates are (no
        // automatic Mastermind/Black reveal). The fusebox pin is NOT part of this - that stays mirrored
        // regardless. "!r" in the trial (see ChooseSide) is a deliberate exception: it always reveals her team,
        // this flag does not affect it. HandWeapon never touches this either; it only ever sends its own banner.
        internal const bool IsNeutral = true;

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

        /// <summary>A player just became Black (took the knife): the accomplice learns about them too (unless neutral).</summary>
        internal static void OnBecameBlack(SPlayer black)
        {
            if (SpecialId == None || IsNeutral) return;
            var room = GameRoom.Instance;
            if (room == null) return;

            SPlayer accomplice = room.Players.FirstOrDefault(IsSpecial);
            if (accomplice == null || accomplice == black || !accomplice.IsAlive) return;
            NotifyKnownBlack(accomplice, black);
        }

        /// <summary>
        /// The Mastermind handed the knife directly to a White player (Player.HandWeapon), instead of it being
        /// picked up from the armory - a deliberate move (often to dodge blame). Always tells the accomplice,
        /// neutral or not, and never changes IsNeutral (that is config-only, see the field). Shown as an
        /// on-screen banner (S_SYSTEM_MESSAGE, ESystemMessageType.NewBlack - the game's own "Someone has just
        /// become the Shadow." text), not a chat line: it appears immediately, no terminal to open, and -
        /// unlike S_NOTIFY_BLACK - carries no player id, so it never names anyone.
        /// </summary>
        internal static void OnKnifeHandedOff(SPlayer mastermind, SPlayer newBlack)
        {
            if (SpecialId == None) return;
            var room = GameRoom.Instance;
            if (room == null) return;

            SPlayer special = room.Players.FirstOrDefault(IsSpecial);
            if (special == null || special == newBlack || special == mastermind || special.IsDummy || special.Session == null || !special.IsAlive) return;

            room.AlertMessage(special, ESystemMessageType.NewBlack);
            Plugin.Print($"Knife handed off by '{mastermind.Name}': accomplice notified (no names)");
        }

        /// <summary>
        /// The game shows the weapon (knife) and fusebox sabotage pins on the minimap only to the Mastermind
        /// (S_SABOTAGE_MISSION). Mirror the fusebox pin to the accomplice so she sees the same one - stays
        /// mirrored even when IsNeutral is on; only teammate identity reveals are affected by it. The knife
        /// (ScWeapon) is deliberately NOT mirrored to her - she never gets to see where it is, including the
        /// 30-seconds-early spoiler BeforeScheduleFirstWeaponSpawn sends the real Mastermind (same packet).
        /// </summary>
        internal static void MirrorMissionPin(GameRoom room, ESchoolMission type, int deviceId, PosInfo pos, bool isAdd)
        {
            if (SpecialId == None || pos == null || type == ESchoolMission.ScWeapon) return;

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

            _offerOpen = false;
            _offerDone = false;
            _offerSerial++;
        }

        // ---------- trial: "!r" reveals her team ----------
        //
        // She stays Dark always and can never vote, full stop - blocked on the HOST regardless of anything
        // client-side (see BeforeHandleTrialEvent in Patches.cs).

        private const int SideChoiceSeconds = 60;
        private const int SideChoiceStepSeconds = 10;

        private static bool _offerOpen;            // the offer window is open
        private static bool _offerDone;            // the offer was already made in this trial
        private static int _offerSerial;           // invalidates the window/reminders of an earlier trial or a finished reveal

        /// <summary>
        /// The trial discussion has started: within the time limit the accomplice may type "!r" to learn who
        /// her team is. There is no way to instantly side with White either - "!r" only ever reveals her team,
        /// it never changes her color, her vote rights (always blocked regardless, see BeforeHandleTrialEvent
        /// in Patches.cs) or how the round scores her. A reminder repeats every 10 seconds; if time runs out
        /// with no input, nothing happens.
        /// </summary>
        internal static void OnTrialDiscuss(GameRoom room)
        {
            if (SpecialId == None || !Plugin.Enabled.Value || _offerOpen || _offerDone) return;

            SPlayer special = room.Players.FirstOrDefault(IsSpecial);
            if (special == null || !special.IsAlive || special.IsDummy || special.Session == null)
            {
                V("team reveal offer skipped (no living real accomplice)");
                return;
            }

            _offerDone = true;
            _offerOpen = true;
            int serial = ++_offerSerial;

            AnnounceReveal(room, special, SideChoiceSeconds);
            ScheduleRevealReminder(room, serial, SideChoiceSeconds);
        }

        private static void AnnounceReveal(GameRoom room, SPlayer special, int secondsRemaining)
        {
            // Normal (trial) chat, broadcast to everyone, shown as coming from the accomplice.
            room.Broadcast(new S_CHAT_MESSAGE
            {
                Type = EChatType.NormalChat,
                Text = $"INPUT !r to learn who your team is. {secondsRemaining} SECONDS REMAINING",
                PlayerId = special.PublicInfo.PlayerId,
                IsDead = false
            });
            Plugin.Print($"Team reveal reminder for '{special.Name}': {secondsRemaining}s left");
        }

        private static void ScheduleRevealReminder(GameRoom room, int serial, int secondsLeft)
        {
            room.PushAfter(SideChoiceStepSeconds * 1000, delegate
            {
                if (serial != _offerSerial || !_offerOpen) return; // trial over, or already used

                int remaining = secondsLeft - SideChoiceStepSeconds;
                if (remaining <= 0)
                {
                    _offerOpen = false;
                    V("team reveal offer: time is up, nothing happens");
                    return;
                }

                SPlayer special = room.Players.FirstOrDefault(IsSpecial);
                if (special == null || !special.IsAlive || special.Session == null)
                {
                    V("team reveal reminder skipped (accomplice no longer available)");
                    return;
                }
                AnnounceReveal(room, special, remaining);
                ScheduleRevealReminder(room, serial, remaining);
            });
        }

        // Gap between the Shadow banner and the Mastermind banner she gets in a row - see ChooseSide - so the
        // second doesn't stack on top of the first on screen.
        private const int SideRevealBannerGapMs = 2500;

        /// <summary>"!r" in the trial chat. Silently ignored outside the offer window.</summary>
        private static void ChooseSide(SPlayer me)
        {
            var room = GameRoom.Instance;
            if (!_offerOpen || room.State != EGameState.Trial || !me.IsAlive)
            {
                V($"!r ignored: no open offer (open={_offerOpen}, state={room.State}, alive={me.IsAlive})");
                return;
            }

            _offerOpen = false;

            // Learning her team is the only effect: no color change, no vote rights, no change to how the
            // round scores her - she stays Dark the whole time.
            //  1) A private line, only she sees it.
            //  2) The game's own native "<name> has become the Shadow." banner for each teammate, as a bonus -
            //     always the simple Dark-branch wording, since her own color never changes. She gets TWO of
            //     these in a row (Shadow, then Mastermind) where everyone else who ever gets one only gets
            //     one, so they are spaced apart to stop the second from stacking on top of the first.
            SPlayer mastermind = room.MasterMind;
            SPlayer shadow = TrialManager.Instance?.Black;
            int serial = _offerSerial; // snapshot: aborts the delayed banner if the trial ends first

            var known = new List<string>();
            if (shadow != null && shadow != me) known.Add($"{shadow.Name} - Shadow");
            if (mastermind != null && mastermind != me) known.Add($"{mastermind.Name} - Mastermind");
            if (known.Count > 0) Tell(me, string.Join(", ", known));
            else V("!r used but no separate Mastermind/Shadow to reveal");

            Plugin.Print("!r used: team revealed");

            if (shadow != null && shadow != me) NotifyKnownBlack(me, shadow);
            else V("!r used but no separate Shadow to reveal");

            room.PushAfter(SideRevealBannerGapMs, delegate
            {
                if (serial != _offerSerial)
                {
                    V("delayed Mastermind banner skipped: trial already exited");
                    return;
                }
                if (mastermind != null && mastermind != me) NotifyKnownBlack(me, mastermind);
                else V("!r used but no separate Mastermind to reveal");
            });

            // Her teammates are not told WHO she is (her Madeline skin already gives that away) or shown any
            // map pin - just a plain heads-up that she picked their side.
            if (mastermind != null && mastermind != me) Tell(mastermind, "Madeline chose your side.");
            if (shadow != null && shadow != me && shadow != mastermind) Tell(shadow, "Madeline chose your side.");
        }

        /// <summary>The trial is over (going to Survive / results): close the reveal offer window.</summary>
        internal static void OnTrialExit(GameRoom room)
        {
            _offerOpen = false;
            _offerDone = false;
            _offerSerial++;
        }

        /// <summary>
        /// PlayerId a clue is recorded under (see Device.RecordLastUsingPlayer). Clues are stored by PlayerId only and the
        /// clients resolve name and picture from it when the clue is viewed, so while the accomplice is shapeshifted his
        /// clues are recorded under the player he imitates. Otherwise they would always point at Madeline.
        /// </summary>
        // Same sentinel PlayerId the game's own Fusebox.cs hardcodes (Define.EWhatState.Mastermind -> 11037,
        // "WhoMastermind" in the proposition sentence/"who" lookup) - not a real player, so it can never
        // collide with an actual PlayerId.
        private const int MastermindSentinelId = 11037;

        internal static int ClueOwnerId(SPlayer p)
        {
            int own = p.PublicInfo.PlayerId;
            if (_formActive && _formTargetId > 0 && IsSpecial(p))
            {
                V($"clue recorded under pid {_formTargetId} (imitated player) instead of pid {own}");
                return _formTargetId;
            }
            if (IsSpecial(p))
            {
                // Not shapeshifted right now: a clue recorded under her own pid would render as Madeline, who
                // has no portrait art for that UI (the same problem Fusebox's own hardcoded 11037 sidesteps by
                // design). Reuse that exact sentinel so all her un-shapeshifted clues render the same generic,
                // working way Fusebox's already do.
                V($"clue recorded under sentinel {MastermindSentinelId} (WhoMastermind) instead of Madeline's own pid {own}");
                return MastermindSentinelId;
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

            // She is always neutral (see IsNeutral): no automatic teammate notification here. "!r" in the
            // trial (ChooseSide) is the only way she ever learns her team.
            V("teammate notification skipped (accomplice is always neutral)");
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
            if (!IsSpecial(sender) || string.IsNullOrEmpty(text)) return false;

            // Some IMEs (e.g. Chinese input methods) insert stray spaces around "!" and the argument
            // (" !3", "! 3", "!  w" ...); Trim() also covers full-width spaces (U+3000), which some IMEs use.
            text = text.Trim();
            if (text.Length == 0) return false;

            // CJK IMEs in "full-width punctuation" mode turn "!" into "！" (U+FF01) and can do the same to digits
            // (e.g. "3" -> "３", U+FF10-U+FF19); accept both so those players do not silently lose the command.
            char first = text[0];
            if (first != '!' && first != '！') return false;

            string body = text.Substring(1).Trim();
            body = NormalizeFullWidthDigits(body);
            V($"chat from accomplice: '{text}' device={deviceId} secret={isSecret} roomState={GameRoom.Instance.State} " +
              $"playerState={sender.State} alive={sender.IsAlive} migrating={GameRoom.Instance.IsMigrating} transitioning={GameRoom.Instance.IsTransitioning}");
            if (body.Length == 0) return false;

            bool isHelp = body.Equals("help", StringComparison.OrdinalIgnoreCase);
            bool isNumber = body.All(c => c >= '0' && c <= '9');
            bool isReveal = body.Equals("r", StringComparison.OrdinalIgnoreCase);
            if (!isHelp && !isNumber && !isReveal) return false; // an ordinary message, not our command

            // Trial: reveal her team. Always swallowed so that nobody sees "!r".
            if (isReveal)
            {
                ChooseSide(sender);
                return true;
            }

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

        /// <summary>Turns full-width digits (U+FF10-U+FF19, "０"-"９") into plain ASCII digits.</summary>
        private static string NormalizeFullWidthDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            char[] chars = null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < '０' || c > '９') continue;
                chars ??= s.ToCharArray();
                chars[i] = (char)('0' + (c - '０'));
            }
            return chars != null ? new string(chars) : s;
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
