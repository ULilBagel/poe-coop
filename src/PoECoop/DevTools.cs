using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Json;
using AI.Player;
using OEIFormats.FlowCharts;
using PoECoop.Net;
using PoECoop.Sync;
using Steamworks;
using UnityEngine;

namespace PoECoop
{
    [Serializable]
    public class ProbeVector { public float x, y, z; }

    [Serializable]
    public class ProbeScreen { public float x, y; public bool onScreen; }

    [Serializable]
    public class ProbeEntity
    {
        public string name, kind, id;
        public ProbeVector world;
        public ProbeScreen screen;
        public bool usable, visible;
    }

    [Serializable]
    public class ProbeStatusEffect
    {
        public uint effectID;
        public int bundleId, originDisplayID;
        public string name, bundleName, origin, originKind, originId, source, sourceId, tag, keyword, stat, error;
        public float duration, timeLeft, timeActive, timeApplied;
        public bool applied, suppressed, suspended, tacticalClear;
    }

    [Serializable]
    public class ProbeTacticalSettings
    {
        public float effectTimePerRound, immediateTimeThreshold, enterCombatBaseRecovery;
        public float delayTurnStep, nextTurnStep, movementTimePerRound;
        public float baseRecovery, postBaseRecovery, baseRecoveryMultiplier, reloadMultiplier;
        public float weakestInterrupt, weakerInterrupt, weakInterrupt, averageInterrupt;
        public float strongInterrupt, strongerInterrupt, strongestInterrupt;
        public float intellectDurationBonus, dualWieldingMultiplier;
        public int visibleRounds;
        public bool unlimitedMovement, unlimitedActions, autoPassTurns;
    }

    [Serializable]
    public class ProbeTacticalActor
    {
        public string name, id, instanceId, type;
    }

    [Serializable]
    public class ProbeTacticalEvent
    {
        public long hostId;
        public int index, roundOffset, initiativeBias, castAttackOccurrence;
        public string kind, type, phase, passStyle, castAttackKey, castAttackName, error;
        public ProbeTacticalActor actor;
        public bool current, active, preview, visible, obscured, useSafeDestroy;
        public bool skipTurnStart, skipTurnEnd, surpriseRound;
        public float initiativeDelay, initiativeSort, previewInitiativeModifier;
        public float timeOutDuration, timeOutDurationRealTime, spinUpDuration, spinDownDuration;
        public float timer, realTimer, attackTime;
    }

    [Serializable]
    public class ProbeTacticalLock
    {
        public string name, type, ownerId;
        public float timeOutTime, timeLeft;
    }

    [Serializable]
    public class ProbeTacticalButton
    {
        public string kind, name, actorId, queuedActorId, error;
        public bool enabled, active;
        public ProbeScreen clientScreen;
    }

    [Serializable]
    public class ProbeTacticalCounter
    {
        public string source, name;
        public long count;
    }

    [Serializable]
    public class ProbeTacticalState
    {
        public string mode, pendingReason, phase, pendingPhase, phaseSource, fullSource, error;
        public long epoch, sequence = -1, currentEventId, pendingEpoch, pendingSequence = -1;
        public bool enabled, shouldBeInCombat, inCombat, applyingHostState, full, fullKnown;
        public bool syncMetadataAvailable, syncInstanceAvailable;
        public bool turnLocked, refreshCurrentEventLocked, spinDownComplete = true;
        public bool hasRoundStart, partyInCombat, isPlayerTurn, currentActorIsWhoseTurn;
        public int eventCount, omittedEventCount, lockCount, omittedLockCount;
        public float effectTime, currentEventTimer, currentEventRealTimer, spinUpTimer;
        public ProbeTacticalSettings settings;
        public ProbeTacticalEvent currentEvent;
        public ProbeTacticalActor currentActor, whoseTurn;
        public ProbeTacticalButton passButton, delayButton;
        public List<ProbeTacticalLock> locks = new List<ProbeTacticalLock>();
        public List<ProbeTacticalEvent> events = new List<ProbeTacticalEvent>();
        public List<ProbeTacticalCounter> blockedGuestMutators = new List<ProbeTacticalCounter>();
    }

    [Serializable]
    public class ProbeAbility
    {
        public string name, normalizedName, cooldownType, whyNotReady, modalGroup, actionType, error;
        public int displayID, occurrence, usesLeft;
        public float cooldown, maxCooldown, modalRecovery, attackTime, previewRecoveryTime;
        public bool ready, readyForUI, unlimitedUses, inCooldownRecovery, inCooldownAtMax;
        public bool modal, activated, uiActivated, applied, inModalRecovery;
        public bool hasAttack, isLongCast, isFreeAction, wasFreeActionUsed;
    }

    [Serializable]
    public class ProbeCombatCharacter
    {
        public string instanceId;
        public float currentHealth, maxHealth, currentStamina, maxStamina;
        public bool dead, unconscious;
        public List<ProbeStatusEffect> effects = new List<ProbeStatusEffect>();
        public List<ProbeAbility> abilities = new List<ProbeAbility>();
        public string desiredAction, currentAction, currentReaction, tacticalError;
        public int desiredActionVariation, currentActionVariation;
        public float desiredActionSpeed, currentActionSpeed;
        public bool desiredActionOffhand, currentActionOffhand;
        public float currentInitiative, tempInitiative, previousBaseInitiative, previousBaseReload;
        public float previousInitiativeIncrement, initiativeDelay, initiativeDelayFromReload;
        public float baseInitiative, baseInitiativeNoReload, availableMovement, availableMoveDistance, cachedAvailableMoveDistance;
        public float moverDesiredSpeed, moverCurrentSpeed, moverRunSpeed, moverWalkSpeed;
        public int turnEventCount, freeActionCount;
        public bool availableAction, isTakingTurn, inTacticalCombat, canPass, canDelay;
        public bool moverEnabled, moverFrozen;
        public bool usedFreeAction, usedFreePotion, switchedWeaponsThisTurn, evaluateTarget;
        public List<string> freeActions = new List<string>();
    }

    [Serializable]
    public class ProbeParty : ProbeCombatCharacter
    {
        public int slot, owner;
        public string name, id, ownerName, state, defaultState, queuedState;
        public bool mine, selected;
        public ProbeVector world, moveDestination;
        public ProbeScreen screen;
        public ProbeEntity target;
    }

    [Serializable]
    public class ProbeWorldCharacter : ProbeCombatCharacter
    {
        public string name, id, state, defaultState, queuedState;
        public ProbeVector world;
        public ProbeScreen screen;
    }

    [Serializable]
    public class ProbeAbilityButton
    {
        public string name, ownerName, ownerId, ownerInstanceId, action;
        public int buttonIndex, subLevel, targetIndex;
        public bool disabled;
        public ProbeAbility ability;
        public ProbeScreen clientScreen;
    }

    [Serializable]
    public class ProbeValue
    {
        public string name, type, value;
    }

    [Serializable]
    public class ProbeFlowPeer
    {
        public int playerId = -1;
        public long worldEpoch = -1;
        public string name, transferId, scene, phase;
        public bool ready, worldReady, barrierReady;
        public List<ProbeValue> values = new List<ProbeValue>();
    }

    [Serializable]
    public class ProbeFlowState
    {
        public bool available, instanceAvailable, worldReady, barrier, barrierOpen;
        public long worldEpoch = -1, stateSeq = -1;
        public string transferId, transferState, transferScene, error;
        public int peerCount, omittedPeerCount;
        public List<ProbeFlowPeer> peers = new List<ProbeFlowPeer>();
        public List<ProbeValue> sync = new List<ProbeValue>();
    }

    [Serializable]
    public class ProbeTimeState
    {
        public bool syncAvailable, instanceAvailable, hasGuestSnapshot, barrierPaused, turnLocked;
        public long worldEpoch = -1, stateSeq = -1, guestStateSeq = -1;
        public bool playerPaused, uiPaused, logicalPaused, paused, prohibitPause, slow, fast, safeToPause;
        public float requestedScale, effectiveScale, normalScale, slowScale, fastScale, overrideScale, debugScale;
        public float combatScale, resumeScale, realtimeSinceStartup;
        public string worldClock, worldDate;
        public int worldYear, worldMonth, worldDay, worldHour, worldMinute, worldSecond, frameWorldSeconds;
        public int gameSecondsPerRealSecond;
        public float worldSecondsRemainder, realWorldPlayTime;
        public string error;
        public List<ProbeValue> sync = new List<ProbeValue>();
    }

    [Serializable]
    public class ProbeDialogueResponse
    {
        public int index, nodeId = -1, paragraph = -1;
        public string text;
        public bool qualified, selected;
        public ProbeScreen clientScreen;
    }

    [Serializable]
    public class ProbeDialogueConversation
    {
        public string id, file, ownerId, ownerInstanceId, displayMode;
        public int startNode = -1, currentNode = -1;
        public float timer;
        public bool completed, disableVO;
    }

    [Serializable]
    public class ProbeDialogueState
    {
        public bool syncAvailable, visible, conversationVisible, interactionVisible, showResponses;
        public long worldEpoch = -1, stateSeq = -1;
        public int pickerPlayerId = -1, syncConversationId = -1, syncNodeId = -1, nextNodeId = -1;
        public int activeCount, omittedConversationCount, responseCount, omittedResponseCount;
        public string activeId, file, picker, nodeText, visibleText, error;
        public ProbeDialogueConversation active;
        public List<ProbeDialogueConversation> conversations = new List<ProbeDialogueConversation>();
        public List<ProbeDialogueResponse> responses = new List<ProbeDialogueResponse>();
        public ProbeTacticalButton continueButton;
        public List<ProbeValue> sync = new List<ProbeValue>();
    }

    [Serializable]
    public class ProbeAreaTransition
    {
        public string id, name, linkedScene, linkedMapTag, startPoint, startPointOverride;
        public bool active, waiting, usable, sameMap;
        public int waitingCount;
        public ProbeVector world;
        public ProbeScreen screen;
    }

    [Serializable]
    public class ProbeAreaStartPoint
    {
        public string id, name, location;
        public bool chosen;
        public ProbeVector world;
        public ProbeScreen screen;
    }

    [Serializable]
    public class ProbeAreaState
    {
        public bool syncAvailable, loading, fadeActive;
        public long worldEpoch = -1, stateSeq = -1;
        public float fadeValue;
        public string state, pendingKind, currentScene, nextScene, requestedStartPoint, requestedStartPointName, error;
        public int pendingRequestId = -1;
        public int transitionCount, omittedTransitionCount, startPointCount, omittedStartPointCount;
        public List<ProbeAreaTransition> transitions = new List<ProbeAreaTransition>();
        public List<ProbeAreaStartPoint> startPoints = new List<ProbeAreaStartPoint>();
        public List<ProbeValue> sync = new List<ProbeValue>();
    }

    [Serializable]
    public class ProbeRestPartyMember
    {
        public string actorId, name, fatigue;
        public int survival, survivalBonus, survivalSubBonus;
    }

    [Serializable]
    public class ProbeRestState
    {
        public bool syncAvailable, visible, movieVisible, moviePlaying, partyInRestZone, canCamp;
        public long worldEpoch = -1, stateSeq = -1;
        public string transactionId, phase, mode, movieMode, sourceId, cannotCampReason, error;
        public int transactionRevision = -1, initiatorPlayerId = -1, roomIndex = -1, startTime = -1, endTime = -1, advancedSeconds;
        public bool committed;
        public int supplies, maxSupplies;
        public float currency;
        public List<ProbeRestPartyMember> party = new List<ProbeRestPartyMember>();
        public ProbeTacticalButton restButton, stashButton, cancelButton;
        public List<ProbeValue> sync = new List<ProbeValue>();
    }

    [Serializable]
    public class ProbeItemMod
    {
        public string name, displayName, category;
        public int displayStringId = -1, cost;
        public bool cursesItem;
    }

    [Serializable]
    public class ProbeItem
    {
        public string itemId, prefabItemId, prefabName, name, displayName, type, filter, equipmentSlot;
        public int displayStringId = -1, descriptionStringId = -1, stack, slot = -1, maxStack;
        public bool original, areaLootSource, equipped, quest, ingredient, unique, canSell;
        public float value;
        public List<ProbeItemMod> mods = new List<ProbeItemMod>();
        public int omittedModCount;
        public bool consumable;
        public string consumableType, usageType;
        public int usageCount, usesRemaining, consumableLevel;
        public bool grimoire;
        public string grimoireOwner;
        public List<string> grimoireSpells = new List<string>();
        public int omittedGrimoireSpellCount;
        public bool hasSoulbind, soulbound, cannotUnbind, unlocksComplete;
        public string soulboundTo, soulboundOwner, soulboundClass;
        public int soulbindUnlockLevel, soulbindUnlockCount;
        public float soulbindProgress, soulbindDegenerateProgress;
        public string error;
    }

    [Serializable]
    public class ProbeInventory
    {
        public string kind, inventoryId, ownerId, ownerInstanceId, ownerName;
        public int maxItems, itemCount, omittedItemCount;
        public List<ProbeItem> items = new List<ProbeItem>();
    }

    [Serializable]
    public class ProbeEquipment
    {
        public string ownerId, ownerInstanceId, ownerName;
        public int selectedWeaponSet, slotCount;
        public List<ProbeItem> items = new List<ProbeItem>();
    }

    [Serializable]
    public class ProbeUiControl
    {
        public string kind, name, text, ownerId, inventoryId, itemId, equipmentSlot;
        public int slot = -1, responseIndex = -1, nodeId = -1;
        public bool active, enabled, blocked, empty, equipped;
        public ProbeScreen clientScreen;
        public string error;
    }

    [Serializable]
    public class ProbeLootState
    {
        public bool syncAvailable, visible, selectedStash;
        public long worldEpoch = -1, stateSeq = -1, revision = -1;
        public int sessionCount, viewerCount, containerCount, omittedContainerCount;
        public string activeSessionId, containerId, selectedActorId, error;
        public List<ProbeInventory> containers = new List<ProbeInventory>();
        public ProbeTacticalButton takeAllButton;
        public ProbeUiControl closeButton;
        public List<ProbeValue> sync = new List<ProbeValue>();
    }

    [Serializable]
    public class ProbeInventoryState
    {
        public bool syncAvailable, visible, stashVisible, dragging;
        public long worldEpoch = -1, stateSeq = -1, revision = -1;
        public string selectedActorId, draggedItemId, error;
        public int inventoryCount, equipmentCount, itemCount, itemControlCount, omittedItemControlCount;
        public List<ProbeInventory> inventories = new List<ProbeInventory>();
        public List<ProbeEquipment> equipment = new List<ProbeEquipment>();
        public List<ProbeUiControl> itemControls = new List<ProbeUiControl>();
        public List<ProbeValue> sync = new List<ProbeValue>();
    }

    [Serializable]
    public class ProbeSteamState
    {
        public bool requested, runtimeAvailable, lobbyServiceAvailable;
        public bool available, running, initialized, nativeAvailable, nativeInitialized;
        public bool transportAvailable;
        public string status, nativeStatus, lobbyStatus, transportType, error;
        public int lobbyMemberCount = -1, lobbyMemberLimit = -1, sessionPlayerCount;
        public int transportConnectionCount = -1, transportLinkCount = -1;
        public List<ProbeValue> values = new List<ProbeValue>();
    }

    [Serializable]
    public class ProbeFrame
    {
        public string utc, role, scene, cursor, desiredCursor, uiTarget;
        public float gameTime, fps;
        public int screenWidth, screenHeight;
        public bool paused, inCombat, gameOver;
        public ProbeSteamState steam;
        public ProbeScreen mouse, worldPickScreen;
        public ProbeVector worldPick;
        public ProbeEntity cursorTarget;
        public ProbeTacticalState tactical;
        public ProbeFlowState flow;
        public ProbeTimeState time;
        public ProbeDialogueState dialogue;
        public ProbeAreaState area;
        public ProbeRestState rest;
        public ProbeLootState loot;
        public ProbeInventoryState inventory;
        public List<ProbeParty> party = new List<ProbeParty>();
        public List<ProbeWorldCharacter> worldCharacters = new List<ProbeWorldCharacter>();
        public List<ProbeAbilityButton> abilityButtons = new List<ProbeAbilityButton>();
        public List<ProbeEntity> usables = new List<ProbeEntity>();
    }

    /// <summary>
    /// Command-line switches for unattended tests. None of this runs in normal play.
    ///   --coop-walk            keep ordering the local player's characters to walk back and forth
    ///                          (on a guest the orders travel to the host like real ones)
    ///   --coop-walk-interval S seconds between those orders (default 5; under 3.5 redirects mid-walk)
    ///   --coop-give-slot N     host: give the party member in slot N to the first guest
    ///   --coop-click-container guest: every few seconds click the nearest container, through the game's own click handler
    ///   --coop-forge-order     guest: every few seconds send a move order for a character it does not control
    ///   --coop-log-positions   write the party's positions to the --coop-log file every second
    ///   --coop-probe FILE      write live input, Steam, combat, shared-flow, inventory, and UI observations to FILE as JSON lines (disabled normally)
    ///   --coop-steam-lobby ID  ask the Steam lobby service to join ID; never hosts or invites
    ///   --coop-tactical-smoke  suppress Odema's native party-removal attack script for tactical fixture runs
    /// </summary>
    internal static class DevTools
    {
        private const int MaxUpdateTraceEntriesPerFrame = 256;
        private static float s_walkInterval = 5f;
        private static bool s_probeEnabled;
        private static float s_nextProbeHeartbeat;
        private static float s_updateTraceUntil;
        private static int s_updateTraceFrame = -1;
        private static int s_updateTraceEntries;
        private static bool s_updateTraceTruncated;
        private static Ownership s_ownership;
        private static Orders s_orders;

        public static void Start(MonoBehaviour runner, Session session, Ownership ownership, Orders orders)
        {
            s_ownership = ownership;
            s_orders = orders;
            string[] args = Environment.GetCommandLineArgs();
            int interval = Array.IndexOf(args, "--coop-walk-interval");
            if (interval >= 0 && interval + 1 < args.Length) float.TryParse(args[interval + 1], out s_walkInterval);
            if (Array.IndexOf(args, "--coop-walk") >= 0) runner.StartCoroutine(Walk(session));
            int give = Array.IndexOf(args, "--coop-give-slot");
            if (give >= 0 && give + 1 < args.Length && int.TryParse(args[give + 1], out int slot))
                runner.StartCoroutine(GiveSlot(session, slot));
            if (Array.IndexOf(args, "--coop-forge-order") >= 0) runner.StartCoroutine(ForgeOrders(session));
            if (Array.IndexOf(args, "--coop-click-container") >= 0) runner.StartCoroutine(ClickContainer(session));
            if (Array.IndexOf(args, "--coop-log-positions") >= 0) runner.StartCoroutine(LogPositions(session));
            int probe = Array.IndexOf(args, "--coop-probe");
            s_probeEnabled = probe >= 0 && probe + 1 < args.Length;
            if (s_probeEnabled) runner.StartCoroutine(Probe(session, args[probe + 1]));
            int steamLobby = Array.IndexOf(args, "--coop-steam-lobby");
            if (steamLobby >= 0)
            {
                string value = steamLobby + 1 < args.Length ? args[steamLobby + 1] : null;
                if (ulong.TryParse(value, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out ulong lobbyId) && lobbyId != 0)
                    runner.StartCoroutine(JoinSteamLobby(session, lobbyId));
                else session.Note("TEST --coop-steam-lobby requires a nonzero unsigned 64-bit lobby ID");
            }
            if (Array.IndexOf(args, "--coop-tactical-smoke") >= 0) runner.StartCoroutine(TacticalSmokeFixture());
        }

        private static IEnumerator JoinSteamLobby(Session session, ulong lobbyId)
        {
            string[] commandLine = Environment.GetCommandLineArgs();
            int nameArg = Array.IndexOf(commandLine, "--coop-name");
            string playerName = nameArg >= 0 && nameArg + 1 < commandLine.Length ? commandLine[nameArg + 1] : session.LocalName;
            if (string.IsNullOrWhiteSpace(playerName)) playerName = "Player";
            float nextDiagnostic = 0f;
            while (true)
            {
                ProbeFeatureContext service = SteamLobbyService();
                if (service.Available && !service.Boolean(false, "Available", "IsAvailable"))
                {
                    if (Time.unscaledTime >= nextDiagnostic)
                    {
                        nextDiagnostic = Time.unscaledTime + 10f;
                        session.Note("TEST waiting for Steam availability (--coop-steam-lobby)");
                    }
                    yield return new WaitForSecondsRealtime(0.5f);
                    continue;
                }
                object target = null;
                object[] arguments = null;
                MethodInfo join = FindSteamJoin(service, lobbyId, playerName, ref target, ref arguments);
                if (join != null)
                {
                    object result;
                    try { result = join.Invoke(target, arguments); }
                    catch (Exception ex)
                    {
                        TargetInvocationException invocation = ex as TargetInvocationException;
                        if (invocation != null && invocation.InnerException != null) ex = invocation.InnerException;
                        session.Note("TEST Steam lobby join request failed (" + ex.GetType().Name + ")");
                        yield break;
                    }
                    session.Note("TEST Steam lobby join requested (--coop-steam-lobby)");
                    IEnumerator nested = result as IEnumerator;
                    if (nested != null)
                    {
                        while (nested.MoveNext()) yield return nested.Current;
                        yield break;
                    }
                    System.Threading.Tasks.Task task = result as System.Threading.Tasks.Task;
                    if (task != null)
                    {
                        while (!task.IsCompleted) yield return null;
                        if (task.IsFaulted || task.IsCanceled)
                        {
                            session.Note("TEST Steam lobby join did not complete");
                            yield break;
                        }
                        PropertyInfo resultProperty = task.GetType().GetProperty("Result");
                        result = resultProperty != null && resultProperty.PropertyType != typeof(void)
                            ? resultProperty.GetValue(task, null) : null;
                    }
                    if (result is bool && !(bool)result) session.Note("TEST Steam lobby join request was not accepted");
                    yield break;
                }
                if (service.Instance != null)
                {
                    session.Note("TEST Steam lobby service has no compatible lobby join method");
                    yield break;
                }
                if (Time.unscaledTime >= nextDiagnostic)
                {
                    nextDiagnostic = Time.unscaledTime + 10f;
                    session.Note("TEST waiting for Steam lobby service (--coop-steam-lobby)");
                }
                yield return new WaitForSecondsRealtime(0.5f);
            }
        }

        private static ProbeFeatureContext SteamLobbyService()
        {
            return ProbeFeatureContext.Create(
                "PoECoop.Net.SteamLobbyService", "PoECoop.SteamLobbyService",
                "PoECoop.Net.SteamLobby", "PoECoop.SteamLobby");
        }

        private static MethodInfo FindSteamJoin(ProbeFeatureContext service, ulong lobbyId, string playerName,
            ref object target, ref object[] arguments)
        {
            if (!service.Available) return null;
            string[] names = { "JoinLobby", "Join", "JoinById", "JoinByID", "JoinLobbyById", "JoinLobbyByID", "RequestJoin" };
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type type = service.Type; type != null; type = type.BaseType)
            {
                foreach (MethodInfo method in type.GetMethods(flags | BindingFlags.Static | BindingFlags.Instance))
                {
                    if (!NameIs(method.Name, names) || method.IsGenericMethodDefinition) continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length < 1 || parameters.Length > 2) continue;
                    object[] values = SteamJoinArguments(parameters, lobbyId, playerName);
                    if (values == null) continue;
                    if (method.IsStatic) target = null;
                    else
                    {
                        if (service.Instance == null) continue;
                        target = service.Instance;
                    }
                    arguments = values;
                    return method;
                }
            }
            return null;
        }

        private static object[] SteamJoinArguments(ParameterInfo[] parameters, ulong lobbyId, string playerName)
        {
            object[] values = new object[parameters.Length];
            bool lobbySet = false, nameSet = false;
            for (int i = 0; i < parameters.Length; i++)
            {
                Type type = parameters[i].ParameterType;
                if (type.IsByRef) return null;
                string parameterName = parameters[i].Name ?? "";
                bool isName = type == typeof(string) && !nameSet
                    && (parameterName.IndexOf("name", StringComparison.OrdinalIgnoreCase) >= 0 || lobbySet);
                if (isName)
                {
                    values[i] = playerName;
                    nameSet = true;
                    continue;
                }
                if (!lobbySet)
                {
                    object lobby = SteamLobbyArgument(type, lobbyId);
                    if (lobby != null)
                    {
                        values[i] = lobby;
                        lobbySet = true;
                        continue;
                    }
                }
                return null;
            }
            return lobbySet && (parameters.Length == 1 || nameSet) ? values : null;
        }

        private static object SteamLobbyArgument(Type type, ulong lobbyId)
        {
            if (type == typeof(ulong)) return lobbyId;
            if (type == typeof(string)) return lobbyId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (type == typeof(CSteamID)) return new CSteamID(lobbyId);
            return null;
        }

        private static IEnumerator TacticalSmokeFixture()
        {
            while (!InLevel) yield return null;
            foreach (ScriptEvent script in UnityEngine.Object.FindObjectsOfType<ScriptEvent>())
            {
                script.Scripts.Clear();
                script.enabled = false;
            }
            foreach (SoundSetComponent sound in UnityEngine.Object.FindObjectsOfType<SoundSetComponent>()) sound.SetSoundSet(null);
            Plugin.Log.LogInfo("TEST tactical smoke disabled Odema attack scripts");
        }

        private static readonly bool s_multiInstance =
            Array.IndexOf(Environment.GetCommandLineArgs(), "--coop-multi-instance") >= 0
            && Array.IndexOf(Environment.GetCommandLineArgs(), "--coop-vsync") < 0;

        /// <summary>
        /// With vertical sync on, a game window that is hidden behind another one is
        /// only allowed to draw about once a second on this desktop, and the whole game
        /// runs at that pace. Two copies on one PC always have one hidden, so in
        /// multi-instance mode sync is off and the frame rate is capped instead.
        /// (--coop-vsync leaves the game's setting alone, to measure the difference.)
        /// </summary>
        public static void UpdateTrace(string stage)
        {
            if (!s_probeEnabled || Time.unscaledTime > s_updateTraceUntil) return;
            int frame = Time.frameCount;
            if (s_updateTraceFrame != frame)
            {
                s_updateTraceFrame = frame;
                s_updateTraceEntries = 0;
                s_updateTraceTruncated = false;
            }
            if (s_updateTraceEntries >= MaxUpdateTraceEntriesPerFrame)
            {
                if (s_updateTraceTruncated) return;
                s_updateTraceTruncated = true;
                Plugin.Log.LogWarning("TEST update trace truncated after " + MaxUpdateTraceEntriesPerFrame
                    + " entries in frame " + frame + " at " + Time.unscaledTime);
                return;
            }
            s_updateTraceEntries++;
            Plugin.Log.LogInfo("TEST update stage " + stage + " " + Time.unscaledTime);
        }

        public static void KeepRunningWhenHidden()
        {
            if (s_probeEnabled && GameState.IsLoading) s_updateTraceUntil = Time.unscaledTime + 15f;
            if (s_probeEnabled && Time.unscaledTime >= s_nextProbeHeartbeat)
            {
                s_nextProbeHeartbeat = Time.unscaledTime + 5f;
                Plugin.Log.LogInfo("TEST plugin Update heartbeat " + Time.unscaledTime);
            }
            if (!s_multiInstance) return;
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
            if (Application.targetFrameRate != 60) Application.targetFrameRate = 60;
            if (!Application.runInBackground) Application.runInBackground = true;
        }

        private static bool InLevel => GameState.IsInGameSession && !GameState.IsLoading && GameState.s_playerCharacter != null;

        /// <summary>Gives move orders the way Player.IssueCommands does (code-survey.md, section 2).</summary>
        private static IEnumerator Walk(Session session)
        {
            while (!InLevel) yield return null;
            yield return new WaitForSecondsRealtime(3f);
            Vector3 home = GameState.s_playerCharacter.transform.position;
            for (int leg = 0; ; leg++)
            {
                if (InLevel && (session.IsHost || session.IsGuest))
                {
                    if (session.IsHost && TimeController.Instance != null) TimeController.Instance.Paused = false;
                    Vector3 offset = (leg % 2 == 0 ? new Vector3(4f, 0f, 0f) : new Vector3(0f, 0f, 0f));
                    int n = 0;
                    foreach (PartyMemberAI member in PartyMemberAI.PartyMembers)
                    {
                        if (member == null || member.StateManager == null) continue;
                        n++;
                        if (!s_ownership.IsMine(member)) continue;
                        Move move = AIStateManager.StatePool.Allocate<Move>();
                        member.StateManager.PushState(move, true);
                        move.Destination = home + offset + new Vector3(0f, 0f, 1.5f * (n - 1));
                        move.Range = 0.05f;
                    }
                }
                yield return new WaitForSecondsRealtime(s_walkInterval);
            }
        }

        private static IEnumerator GiveSlot(Session session, int slot)
        {
            while (true)
            {
                yield return new WaitForSecondsRealtime(1f);
                if (!InLevel || !session.IsHost || session.Players.Count < 2) continue;
                PartyMemberAI member = PartyMemberAI.PartyMembers[slot];
                if (member == null) continue;
                if (TimeController.Instance != null) TimeController.Instance.Paused = false;
                int guest = session.Players[1].Id;
                if (s_ownership.OwnerOf(member) != guest) s_ownership.Assign(member, guest);
            }
        }

        private static IEnumerator ForgeOrders(Session session)
        {
            while (true)
            {
                yield return new WaitForSecondsRealtime(7f);
                if (!InLevel || !session.IsGuest) continue;
                foreach (PartyMemberAI member in PartyMemberAI.PartyMembers)
                    if (member != null && !s_ownership.IsMine(member))
                    {
                        s_orders.SendMoveUnchecked(member, member.transform.position + new Vector3(0f, 0f, -5f));
                        break;
                    }
            }
        }

        private static IEnumerator ClickContainer(Session session)
        {
            while (true)
            {
                yield return new WaitForSecondsRealtime(9f);
                if (!InLevel || !session.IsGuest) continue;
                Container nearest = null;
                float best = float.MaxValue;
                Vector3 from = GameState.s_playerCharacter.transform.position;
                foreach (Container c in UnityEngine.Object.FindObjectsOfType<Container>())
                {
                    float d = (c.transform.position - from).sqrMagnitude;
                    if (d < best) { best = d; nearest = c; }
                }
                if (nearest == null) { session.Note("TEST no container in this level"); continue; }
                session.Note("TEST clicking container " + nearest.name);
                GameState.s_playerCharacter.ObjectClicked(nearest);
            }
        }

        private static void ProbeTrace(Session session, string stage, ref int trace, int bit)
        {
            int flag = 1 << bit;
            if ((trace & flag) != 0) return;
            trace |= flag;
            session.Note("TEST probe stage " + stage);
        }

        private static IEnumerator Probe(Session session, string file)
        {
            StreamWriter writer = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));
                writer = new StreamWriter(file, false) { AutoFlush = true };
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("--coop-probe could not open " + file + ": " + ex.Message);
                yield break;
            }
            session.Note("TEST live-input probe writing " + file);
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(ProbeFrame));
            using (writer)
            {
                int lastFrame = Time.frameCount;
                float lastTime = Time.unscaledTime;
                float nextDiagnostic = 0f;
                int trace = 0;
                while (true)
                {
                    yield return new WaitForSecondsRealtime(0.5f);
                    if (!InLevel || Camera.main == null)
                    {
                        if (SteamProbeRequested)
                        {
                            WriteProbeFrame(serializer, writer, new ProbeFrame
                            {
                                utc = DateTime.UtcNow.ToString("o"),
                                gameTime = Time.unscaledTime,
                                role = session.IsHost ? "host" : session.IsGuest ? "guest" : "offline",
                                steam = SteamData(session)
                            });
                            ProbeTrace(session, "steam", ref trace, 14);
                        }
                        if (Time.unscaledTime >= nextDiagnostic)
                        {
                            nextDiagnostic = Time.unscaledTime + 5f;
                            session.Note("TEST probe waiting: inLevel=" + InLevel + " camera=" + (Camera.main != null)
                                + " inSession=" + GameState.IsInGameSession + " loading=" + GameState.IsLoading
                                + " player=" + (GameState.s_playerCharacter != null));
                        }
                        continue;
                    }
                    Camera camera = Camera.main;
                    ProbeTrace(session, "frame", ref trace, 0);
                    ProbeFrame frame = new ProbeFrame
                    {
                        utc = DateTime.UtcNow.ToString("o"),
                        gameTime = Time.unscaledTime,
                        role = session.IsHost ? "host" : session.IsGuest ? "guest" : "offline",
                        scene = GameState.Instance != null && GameState.Instance.CurrentMap != null ? GameState.Instance.CurrentMap.SceneName : "",
                        paused = TimeController.Instance != null && TimeController.Instance.Paused,
                        inCombat = GameState.InCombat,
                        gameOver = GameState.GameOver,
                        screenWidth = Screen.width,
                        screenHeight = Screen.height,
                        fps = (Time.frameCount - lastFrame) / Mathf.Max(0.001f, Time.unscaledTime - lastTime),
                        mouse = ScreenPoint(Input.mousePosition),
                        cursor = GameCursor.ActiveCursor.ToString(),
                        desiredCursor = GameCursor.DesiredCursor.ToString(),
                        cursorTarget = EntityData(GameCursor.ObjectUnderCursor, camera),
                        uiTarget = GameCursor.UiObjectUnderCursor != null ? GameCursor.UiObjectUnderCursor.name : "",
                        worldPick = VectorData(GameCursor.WorldPickPosition),
                        worldPickScreen = ScreenPoint(camera, GameCursor.WorldPickPosition),
                        steam = SteamData(session)
                    };
                    ProbeTrace(session, "steam", ref trace, 14);
                    lastFrame = Time.frameCount;
                    lastTime = Time.unscaledTime;
                    frame.tactical = TacticalData();
                    ProbeTrace(session, "tactical", ref trace, 1);
                    frame.flow = FlowData(session);
                    ProbeTrace(session, "flow", ref trace, 2);
                    frame.time = TimeData();
                    ProbeTrace(session, "time", ref trace, 3);
                    frame.dialogue = DialogueData();
                    ProbeTrace(session, "dialogue", ref trace, 4);
                    frame.area = AreaData(camera);
                    ProbeTrace(session, "area", ref trace, 5);
                    frame.rest = RestData();
                    ProbeTrace(session, "rest", ref trace, 6);
                    frame.loot = LootData();
                    ProbeTrace(session, "loot", ref trace, 7);
                    frame.inventory = InventoryData();
                    ProbeTrace(session, "inventory", ref trace, 8);
                    foreach (PartyMemberAI member in PartyMemberAI.PartyMembers)
                    {
                        if (member == null) continue;
                        AIStateManager states = member.StateManager;
                        GameObject target = states != null ? states.CurrentTarget : null;
                        Move move = states != null ? states.FindState(typeof(Move)) as Move : null;
                        ProbeParty party = new ProbeParty
                        {
                            slot = member.Slot,
                            name = CharacterStats.Name(member),
                            id = Ownership.IdOf(member).ToString(),
                            owner = s_ownership.OwnerOf(member),
                            ownerName = session.NameOf(s_ownership.OwnerOf(member)),
                            mine = s_ownership.IsMine(member),
                            selected = member.Selected,
                            world = VectorData(member.transform.position),
                            screen = ScreenPoint(camera, member.transform.position),
                            state = states != null && states.CurrentState != null ? states.CurrentState.GetType().Name : "",
                            defaultState = states != null && states.DefaultState != null ? states.DefaultState.GetType().Name : "",
                            queuedState = states != null && states.QueuedState != null ? states.QueuedState.GetType().Name : "",
                            target = EntityData(target, camera),
                            moveDestination = move != null ? VectorData(move.Destination) : null
                        };
                        CharacterData(party, member.gameObject);
                        frame.party.Add(party);
                    }
                    ProbeTrace(session, "party", ref trace, 9);
                    foreach (Health health in UnityEngine.Object.FindObjectsOfType<Health>())
                    {
                        if (health == null || !health.gameObject.activeInHierarchy) continue;
                        InstanceID instance = health.GetComponent<InstanceID>();
                        if (instance == null) continue;
                        PartyMemberAI member = health.GetComponent<PartyMemberAI>();
                        if (PartyMemberAI.IsInPartyList(member, false)) continue;
                        AIController ai = GameUtilities.FindActiveAIController(health.gameObject);
                        AIStateManager states = ai != null ? ai.StateManager : null;
                        ProbeWorldCharacter character = new ProbeWorldCharacter
                        {
                            name = CharacterStats.Name(health),
                            id = NetworkEntityId(health.gameObject),
                            world = VectorData(health.transform.position),
                            screen = ScreenPoint(camera, health.transform.position),
                            state = states != null && states.CurrentState != null ? states.CurrentState.GetType().Name : "",
                            defaultState = states != null && states.DefaultState != null ? states.DefaultState.GetType().Name : "",
                            queuedState = states != null && states.QueuedState != null ? states.QueuedState.GetType().Name : ""
                        };
                        CharacterData(character, health.gameObject);
                        frame.worldCharacters.Add(character);
                    }
                    ProbeTrace(session, "world", ref trace, 10);
                    GameObject abilityOwner = UIAbilityBar.GetSelectedForBars();
                    CharacterStats abilityOwnerStats = abilityOwner != null ? abilityOwner.GetComponent<CharacterStats>() : null;
                    foreach (UIAbilityBarButton button in UnityEngine.Object.FindObjectsOfType<UIAbilityBarButton>())
                    {
                        if (button == null || !button.gameObject.activeInHierarchy) continue;
                        GenericAbility ability = ButtonAbility(button);
                        ProbeAbilityButton data = new ProbeAbilityButton
                        {
                            name = button.name,
                            ownerName = abilityOwner != null ? CharacterStats.Name(abilityOwner) : "",
                            ownerId = NetworkEntityId(abilityOwner),
                            ownerInstanceId = SavedInstanceId(abilityOwner),
                            action = button.TargetAction.ToString(),
                            buttonIndex = button.Owner != null ? button.Owner.GetButtonIndex(button) : -1,
                            subLevel = button.Owner != null ? button.Owner.SubLevel : -1,
                            targetIndex = button.TargetIndex,
                            disabled = button.Disabled,
                            ability = ability != null ? AbilityData(ability, abilityOwnerStats, AbilityOccurrence(ability, abilityOwnerStats)) : null,
                            clientScreen = WidgetScreen(button.Background != null ? button.Background : button.Icon)
                        };
                        frame.abilityButtons.Add(data);
                    }
                    ProbeTrace(session, "abilities", ref trace, 11);
                    foreach (Usable usable in UnityEngine.Object.FindObjectsOfType<Usable>())
                    {
                        if (usable == null || !usable.gameObject.activeInHierarchy) continue;
                        ProbeEntity entity = EntityData(usable.gameObject, camera, usable.GetType().Name);
                        entity.usable = usable.IsUsable;
                        entity.visible = usable.IsVisible;
                        frame.usables.Add(entity);
                    }
                    ProbeTrace(session, "usables", ref trace, 12);
                    WriteProbeFrame(serializer, writer, frame);
                    ProbeTrace(session, "written", ref trace, 13);
                }
            }
        }

        private static void WriteProbeFrame(DataContractJsonSerializer serializer, StreamWriter writer, ProbeFrame frame)
        {
            using (MemoryStream json = new MemoryStream())
            {
                serializer.WriteObject(json, frame);
                writer.WriteLine(System.Text.Encoding.UTF8.GetString(json.ToArray()));
            }
        }

        private const int MaxProbeFlowPeers = 16;
        private const int MaxProbeSyncValues = 512;
        private const int MaxProbeDialogueConversations = 16;
        private const int MaxProbeDialogueResponses = 64;
        private const int MaxProbeAreaTransitions = 64;
        private const int MaxProbeAreaStartPoints = 64;
        private const int MaxProbeInventories = 32;
        private const int MaxProbeItemsPerInventory = 256;
        private const int MaxProbeItemMods = 64;
        private const int MaxProbeItemControls = 512;

        private sealed class ProbeFeatureContext
        {
            internal Type Type;
            internal object Instance;
            internal bool Available => Type != null;

            internal static ProbeFeatureContext Create(params string[] names)
            {
                foreach (string name in names)
                {
                    Type type = typeof(DevTools).Assembly.GetType(name, false);
                    if (type == null) continue;
                    return new ProbeFeatureContext { Type = type, Instance = FindSyncInstance(type) };
                }
                return new ProbeFeatureContext();
            }

            internal object Value(params string[] names)
            {
                if (Type == null) return null;
                object value = NamedValue(Type, null, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, names);
                if (value != null) return value;
                return Instance != null ? NamedValue(Type, Instance, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, names) : null;
            }

            internal long Int64(long fallback, params string[] names)
            {
                object value = Value(names);
                return TryInt64(value, out long result) ? result : fallback;
            }

            internal bool Boolean(bool fallback, params string[] names)
            {
                object value = Value(names);
                if (value is bool) return (bool)value;
                if (value != null && bool.TryParse(value.ToString(), out bool result)) return result;
                return fallback;
            }

            internal string Text(params string[] names) => BoundedText(Value(names), 512);
        }

        private const int MaxProbeSteamValues = 256;
        private const int MaxProbeSteamCollectionValues = 32;

        private static bool SteamProbeRequested =>
            Array.IndexOf(Environment.GetCommandLineArgs(), "-steam") >= 0
            || Array.IndexOf(Environment.GetCommandLineArgs(), "--coop-steam-lobby") >= 0;

        private static ProbeSteamState SteamData(Session session)
        {
            ProbeSteamState data = new ProbeSteamState
            {
                requested = Array.IndexOf(Environment.GetCommandLineArgs(), "-steam") >= 0,
                sessionPlayerCount = session != null ? session.Players.Count : 0
            };
            ProbeFeatureContext runtime = ProbeFeatureContext.Create(
                "PoECoop.Net.SteamRuntime", "PoECoop.SteamRuntime",
                "PoECoop.Net.SteamService", "PoECoop.SteamService",
                "PoECoop.Net.SteamBackend", "PoECoop.SteamBackend");
            ProbeFeatureContext lobby = SteamLobbyService();
            data.runtimeAvailable = runtime.Available;
            data.lobbyServiceAvailable = lobby.Available;
            data.available = runtime.Boolean(false, "Available", "IsAvailable", "SteamAvailable")
                || lobby.Boolean(false, "Available", "IsAvailable", "SteamAvailable");
            data.running = runtime.Boolean(lobby.Boolean(false, "Running", "IsRunning", "SteamRunning", "IsSteamRunning"),
                "Running", "IsRunning", "SteamRunning", "IsSteamRunning");
            data.initialized = runtime.Boolean(lobby.Boolean(false, "Initialized", "IsInitialized", "SteamInitialized"),
                "Initialized", "IsInitialized", "SteamInitialized");
            data.nativeAvailable = runtime.Boolean(lobby.Boolean(false, "NativeAvailable", "IsNativeAvailable", "NativeLoaded", "NativeLibraryLoaded"),
                "NativeAvailable", "IsNativeAvailable", "NativeLoaded", "NativeLibraryLoaded");
            data.nativeInitialized = runtime.Boolean(lobby.Boolean(false, "NativeInitialized", "IsNativeInitialized", "NativeReady", "IsNativeReady"),
                "NativeInitialized", "IsNativeInitialized", "NativeReady", "IsNativeReady");
            data.status = SafeSteamText(runtime.Value("Status", "State", "Lifecycle", "InitStatus"));
            data.nativeStatus = SafeSteamText(runtime.Value("NativeStatus", "NativeState", "NativeInitStatus"));
            if (string.IsNullOrEmpty(data.nativeStatus))
                data.nativeStatus = SafeSteamText(lobby.Value("NativeStatus", "NativeState", "NativeInitStatus"));
            data.lobbyStatus = SafeSteamText(lobby.Value("Status", "State", "Phase", "LobbyStatus"));
            CaptureNativeInitialization(data, lobby);

            object transport = session != null ? MemberByNames(session, "Transport", "CurrentTransport") : null;
            data.transportAvailable = transport != null;
            data.transportType = transport != null ? transport.GetType().Name : "";
            if (transport != null && transport.GetType().Name.IndexOf("Steam", StringComparison.OrdinalIgnoreCase) >= 0)
                data.available = true;
            if (data.available)
            {
                data.running = true;
                data.initialized = true;
                data.nativeAvailable = true;
                data.nativeInitialized = true;
            }
            if (string.IsNullOrEmpty(data.status)) data.status = data.lobbyStatus;
            if (string.IsNullOrEmpty(data.status)) data.status = SafeSteamText(MemberByNames(transport, "Status", "State"));

            object lobbyMembers = lobby.Value("MemberCount", "MembersCount", "LobbyMemberCount", "NumLobbyMembers");
            data.lobbyMemberCount = ObjectInt(lobbyMembers, -1);
            if (lobbyMembers == null)
            {
                object members = lobby.Value("Members", "LobbyMembers");
                if (members != null) data.lobbyMemberCount = ObjectCount(members);
            }
            if (data.lobbyMemberCount < 0)
                data.lobbyMemberCount = InvokeSteamLobbyCount(lobby, -1, "GetLobbyMemberCount", "GetNumLobbyMembers", "GetMemberCount");
            object lobbyLimit = lobby.Value("MemberLimit", "LobbyMemberLimit", "MaxMembers", "MaxLobbyMembers");
            data.lobbyMemberLimit = ObjectInt(lobbyLimit, -1);
            if (data.lobbyMemberLimit < 0)
                data.lobbyMemberLimit = InvokeSteamLobbyCount(lobby, -1, "GetLobbyMemberLimit", "GetMemberLimit");
            CaptureNativeLobbyCounts(data, lobby);

            object connections = MemberByNames(transport, "ConnectionCount", "ConnectionsCount");
            data.transportConnectionCount = ObjectInt(connections, -1);
            if (connections == null)
            {
                object collection = MemberByNames(transport, "Connections", "Peers");
                if (collection != null) data.transportConnectionCount = ObjectCount(collection);
            }
            object links = MemberByNames(transport, "LinkCount", "LinksCount");
            data.transportLinkCount = ObjectInt(links, -1);
            if (links == null)
            {
                object collection = MemberByNames(transport, "Links", "Sessions", "P2PSessions");
                if (collection != null) data.transportLinkCount = ObjectCount(collection);
            }

            try
            {
                CaptureSteamTypeValues(data.values, runtime.Type, "runtime");
                CaptureSteamValues(data.values, runtime.Instance, "runtime.instance", 0, new List<object>());
                CaptureSteamTypeValues(data.values, lobby.Type, "lobby");
                CaptureSteamValues(data.values, lobby.Instance, "lobby.instance", 0, new List<object>());
                CaptureSteamValues(data.values, transport, "transport", 0, new List<object>());
                CaptureNativeRelayStates(data, transport);
            }
            catch (Exception ex)
            {
                data.error = ex.GetType().Name;
            }
            return data;
        }

        private static void CaptureNativeInitialization(ProbeSteamState data, ProbeFeatureContext lobby)
        {
            if (lobby.Instance == null) return;
            try
            {
                FieldInfo instanceField = lobby.Value("InstanceField") as FieldInfo;
                FieldInfo initializedField = lobby.Value("InitializedField") as FieldInfo;
                if (instanceField == null || initializedField == null) return;
                object instance = instanceField.GetValue(null);
                UnityEngine.Object unityObject = instance as UnityEngine.Object;
                bool hasInstance = instance != null;
                if (instance is UnityEngine.Object) hasInstance = unityObject != null;
                data.nativeInitialized = hasInstance && (bool)initializedField.GetValue(instance);
                data.initialized = data.nativeInitialized;
                if (data.nativeInitialized) data.nativeAvailable = true;
                data.nativeStatus = data.nativeInitialized ? "Initialized" : hasInstance ? "Uninitialized" : "WaitingForSteamManager";
            }
            catch (Exception ex)
            {
                data.nativeStatus = ex.GetType().Name;
            }
        }

        private static void CaptureNativeLobbyCounts(ProbeSteamState data, ProbeFeatureContext lobby)
        {
            if (!data.available || lobby.Instance == null || (data.lobbyMemberCount >= 0 && data.lobbyMemberLimit >= 0)) return;
            object current = lobby.Value("CurrentLobby", "Lobby", "CurrentLobbyId");
            if (!(current is CSteamID)) return;
            CSteamID lobbyId = (CSteamID)current;
            if (lobbyId == CSteamID.Nil) return;
            try
            {
                if (data.lobbyMemberCount < 0) data.lobbyMemberCount = SteamMatchmaking.GetNumLobbyMembers(lobbyId);
                if (data.lobbyMemberLimit < 0) data.lobbyMemberLimit = SteamMatchmaking.GetLobbyMemberLimit(lobbyId);
            }
            catch (Exception ex)
            {
                if (string.IsNullOrEmpty(data.nativeStatus)) data.nativeStatus = ex.GetType().Name;
            }
        }

        private static void CaptureNativeRelayStates(ProbeSteamState data, object transport)
        {
            if (!data.available || transport == null
                || transport.GetType().Name.IndexOf("Steam", StringComparison.OrdinalIgnoreCase) < 0) return;
            object links = MemberByNames(transport, "Links");
            IDictionary dictionary = links as IDictionary;
            IEnumerable values = dictionary != null ? dictionary.Values : links as IEnumerable;
            if (values == null) return;
            int index = 0;
            foreach (object link in values)
            {
                if (index >= MaxProbeSteamCollectionValues || data.values.Count >= MaxProbeSteamValues) break;
                object remoteValue = MemberByNames(link, "Remote", "RemoteSteamId", "SteamId", "PeerId");
                if (!(remoteValue is CSteamID)) continue;
                CSteamID remote = (CSteamID)remoteValue;
                if (remote == CSteamID.Nil) continue;
                string prefix = "transport.nativeRelay[" + index + "]";
                index++;
                try
                {
                    if (SteamNetworking.GetP2PSessionState(remote, out P2PSessionState_t state))
                        CaptureSteamValues(data.values, state, prefix, 0, new List<object>());
                    else AddProbeText(data.values, prefix + ".nativeState", "Unavailable");
                }
                catch (Exception ex)
                {
                    AddProbeText(data.values, prefix + ".nativeError", ex.GetType().Name);
                }
            }
        }

        private static int InvokeSteamLobbyCount(ProbeFeatureContext lobby, int fallback, params string[] names)
        {
            if (lobby.Instance == null) return fallback;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type type = lobby.Type; type != null; type = type.BaseType)
            {
                foreach (MethodInfo method in type.GetMethods(flags))
                {
                    if (!NameIs(method.Name, names) || method.GetParameters().Length != 0) continue;
                    try
                    {
                        object value = method.Invoke(lobby.Instance, null);
                        return TryInt64(value, out long result) ? ObjectInt(value, fallback) : fallback;
                    }
                    catch (Exception) { return fallback; }
                }
            }
            return fallback;
        }

        private static void CaptureSteamTypeValues(List<ProbeValue> output, Type type, string prefix)
        {
            if (type == null) return;
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type current = type; current != null && output.Count < MaxProbeSteamValues; current = current.BaseType)
            {
                foreach (FieldInfo field in current.GetFields(flags))
                {
                    if (!IsSteamDiagnosticMember(field.Name) || typeof(Delegate).IsAssignableFrom(field.FieldType)) continue;
                    try { CaptureSteamValues(output, field.GetValue(null), prefix + "." + NormalizedMemberName(field.Name), 0, new List<object>()); }
                    catch (Exception ex) { AddProbeText(output, prefix + "." + field.Name + ".error", ex.GetType().Name); }
                }
                foreach (PropertyInfo property in current.GetProperties(flags))
                {
                    if (!property.CanRead || property.GetIndexParameters().Length != 0 || !IsSteamDiagnosticMember(property.Name)
                        || typeof(Delegate).IsAssignableFrom(property.PropertyType)) continue;
                    try { CaptureSteamValues(output, property.GetValue(null, null), prefix + "." + property.Name, 0, new List<object>()); }
                    catch (Exception ex) { AddProbeText(output, prefix + "." + property.Name + ".error", ex.GetType().Name); }
                }
            }
        }

        private static void CaptureSteamValues(List<ProbeValue> output, object target, string prefix, int depth, List<object> ancestors)
        {
            if (target == null || output.Count >= MaxProbeSteamValues || depth > 4) return;
            Type targetType = target.GetType();
            string text = target as string;
            if (text != null)
            {
                if (IsSteamTextPath(prefix)) AddProbeText(output, prefix, SafeSteamText(text));
                return;
            }
            if (targetType.IsPrimitive || targetType.IsEnum || target is decimal)
            {
                AddProbeValue(output, prefix, target);
                return;
            }
            if (target is Guid || target is DateTime || target is TimeSpan || target is Session
                || typeof(Delegate).IsAssignableFrom(targetType) || target is UnityEngine.Object) return;
            byte[] bytes = target as byte[];
            if (bytes != null)
            {
                AddProbeValue(output, prefix + ".length", bytes.Length);
                return;
            }
            foreach (object ancestor in ancestors) if (ReferenceEquals(ancestor, target)) return;
            ancestors.Add(target);
            try
            {
                IDictionary dictionary = target as IDictionary;
                if (dictionary != null)
                {
                    AddProbeValue(output, prefix + ".count", dictionary.Count);
                    if (IsSteamIdentityCollection(prefix)) return;
                    int index = 0;
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (index >= MaxProbeSteamCollectionValues || output.Count >= MaxProbeSteamValues) break;
                        CaptureSteamValues(output, entry.Value, prefix + "[" + index + "]", depth + 1, ancestors);
                        index++;
                    }
                    if (dictionary.Count > index) AddProbeValue(output, prefix + ".omitted", dictionary.Count - index);
                    return;
                }
                IEnumerable enumerable = target as IEnumerable;
                if (enumerable != null)
                {
                    if (IsSteamIdentityCollection(prefix))
                    {
                        AddProbeValue(output, prefix + ".count", ObjectCount(target));
                        return;
                    }
                    int index = 0;
                    foreach (object value in enumerable)
                    {
                        if (index >= MaxProbeSteamCollectionValues || output.Count >= MaxProbeSteamValues) break;
                        CaptureSteamValues(output, value, prefix + "[" + index + "]", depth + 1, ancestors);
                        index++;
                    }
                    ICollection collection = target as ICollection;
                    if (collection != null)
                    {
                        AddProbeValue(output, prefix + ".count", collection.Count);
                        if (collection.Count > index) AddProbeValue(output, prefix + ".omitted", collection.Count - index);
                    }
                    return;
                }
                if (targetType.Assembly != typeof(DevTools).Assembly && !targetType.IsValueType) return;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                for (Type type = targetType; type != null && output.Count < MaxProbeSteamValues; type = type.BaseType)
                {
                    foreach (FieldInfo field in type.GetFields(flags))
                    {
                        if (!IsSteamDiagnosticMember(field.Name) || typeof(Delegate).IsAssignableFrom(field.FieldType)) continue;
                        try { CaptureSteamValues(output, field.GetValue(target), prefix + "." + NormalizedMemberName(field.Name), depth + 1, ancestors); }
                        catch (Exception ex) { AddProbeText(output, prefix + "." + field.Name + ".error", ex.GetType().Name); }
                    }
                    foreach (PropertyInfo property in type.GetProperties(flags))
                    {
                        if (!property.CanRead || property.GetIndexParameters().Length != 0 || !IsSteamDiagnosticMember(property.Name)
                            || typeof(Delegate).IsAssignableFrom(property.PropertyType)) continue;
                        try { CaptureSteamValues(output, property.GetValue(target, null), prefix + "." + property.Name, depth + 1, ancestors); }
                        catch (Exception ex) { AddProbeText(output, prefix + "." + property.Name + ".error", ex.GetType().Name); }
                    }
                }
            }
            finally
            {
                ancestors.RemoveAt(ancestors.Count - 1);
            }
        }

        private static bool IsSteamDiagnosticMember(string member)
        {
            string name = NormalizedMemberName(member).ToLowerInvariant();
            if (name == "statustext" || IsPrivateSteamMember(name)) return false;
            string[] included =
            {
                "availab", "initializ", "running", "ready", "status", "state", "phase", "error",
                "native", "relay", "p2p", "lobby", "member", "transport", "link", "connection",
                "queue", "pending", "incoming", "outgoing", "send", "sent", "recv", "receive",
                "packet", "message", "byte", "event", "count", "limit", "backpressure", "drop",
                "reject", "fail", "timeout", "retry", "active", "connect", "closed", "disposed",
                "hosting", "listening"
            };
            foreach (string token in included) if (name.Contains(token)) return true;
            return false;
        }

        private static bool IsSteamIdentityCollection(string path)
        {
            string name = path.ToLowerInvariant();
            string[] identities = { "member", "peer", "friend", "user", "owner", "identity" };
            foreach (string identity in identities) if (name.Contains(identity)) return true;
            return false;
        }

        private static bool IsPrivateSteamMember(string name)
        {
            if (name == "id" || name == "ip" || name == "port" || name == "address" || name == "endpoint"
                || name == "remoteip" || name == "nremoteip" || name == "remoteport" || name == "nremoteport"
                || name == "localport" || name == "connectionid") return true;
            string[] sensitive =
            {
                "steamid", "accountid", "lobbyid", "ownerid", "hostid", "userid", "friendid",
                "persona", "account", "friend", "ticket", "token", "secret", "credential"
            };
            foreach (string token in sensitive) if (name.Contains(token)) return true;
            return name.EndsWith("name", StringComparison.Ordinal)
                || name.Contains("address") || name.Contains("endpoint") || name.Contains("remoteip");
        }

        private static bool IsSteamTextPath(string path)
        {
            string name = path.ToLowerInvariant();
            return name.Contains("status") || name.Contains("state") || name.Contains("phase")
                || name.Contains("mode") || name.Contains("kind");
        }

        private static string SafeSteamText(object value)
        {
            string text = BoundedText(value, 512);
            if (text.Length == 0) return text;
            char[] safe = text.ToCharArray();
            for (int i = 0; i < safe.Length; i++) if (char.IsDigit(safe[i])) safe[i] = '#';
            return new string(safe);
        }

        private static ProbeFlowState FlowData(Session session)
        {
            ProbeFlowState data = new ProbeFlowState();
            ProbeFeatureContext feature = ProbeFeatureContext.Create("PoECoop.Sync.FlowCoordinator", "PoECoop.FlowCoordinator");
            data.available = feature.Available;
            data.instanceAvailable = feature.Instance != null;
            try
            {
                CaptureFeatureValues(data.sync, feature, "flow");
                data.worldEpoch = feature.Int64(-1, "WorldEpoch", "CurrentWorldEpoch", "Epoch");
                data.stateSeq = feature.Int64(-1, "StateSeq", "StateSequence", "Sequence", "SnapshotSequence");
                data.worldReady = feature.Boolean(session != null && session.IsWorldReady, "WorldReady", "IsWorldReady", "AllWorldReady", "AllReady", "Ready");
                data.barrier = feature.Boolean(false, "Barrier", "BarrierActive", "AtBarrier", "WaitingAtBarrier", "ApplyingWorldBarrier");
                data.barrierOpen = feature.Boolean(!data.barrier, "BarrierOpen", "IsBarrierOpen", "BarrierReleased");
                object transfer = feature.Value("Transfer", "CurrentTransfer", "PendingTransfer", "Transition", "PendingTransition", "AreaTransition");
                object transferSource = transfer ?? feature.Instance;
                data.transferId = BoundedText(MemberByNames(transferSource, "TransferId", "TransactionId", "Id"), 128);
                if (string.IsNullOrEmpty(data.transferId)) data.transferId = feature.Text("TransferId", "CurrentTransferId", "CurrentTransfer");
                data.transferState = BoundedText(MemberByNames(transferSource, "State", "Phase", "Status"), 128);
                data.transferScene = BoundedText(MemberByNames(transferSource, "Scene", "SceneName", "TargetScene", "Map", "MapName"), 256);
                string[] peerCollections = { "Peers", "PeerStates", "Readiness", "PeerReadiness", "WorldReadiness", "BarrierPeers", "BarrierReadiness", "Players" };
                foreach (string name in peerCollections) AddFlowPeers(data, feature.Value(name), name, session);
                if (session != null)
                {
                    foreach (PlayerInfo player in session.Players)
                    {
                        if (player == null) continue;
                        ProbeFlowPeer peer = GetFlowPeer(data, player.Id);
                        peer.name = BoundedText(player.Name, 128);
                        bool ready = session.IsPeerReady(player.Id);
                        peer.ready = ready;
                        peer.worldReady = ready;
                        peer.barrierReady = ready;
                        if (string.IsNullOrEmpty(peer.phase) || peer.phase == "Waiting") peer.phase = ready ? "Ready" : "Waiting";
                    }
                }
                if (string.IsNullOrEmpty(data.transferState) && data.transferId != "0")
                {
                    foreach (ProbeFlowPeer peer in data.peers)
                    {
                        if (string.IsNullOrEmpty(peer.phase) || (!string.IsNullOrEmpty(peer.transferId) && peer.transferId != data.transferId)) continue;
                        data.transferState = peer.phase;
                        if (string.IsNullOrEmpty(data.transferScene)) data.transferScene = peer.scene;
                        break;
                    }
                }
                data.peerCount = data.peers.Count + data.omittedPeerCount;
            }
            catch (Exception ex)
            {
                data.error = ErrorText(ex);
            }
            return data;
        }

        private static void AddFlowPeers(ProbeFlowState data, object collection, string source, Session session)
        {
            if (collection == null || collection is string) return;
            IDictionary dictionary = collection as IDictionary;
            if (dictionary != null)
            {
                int inspected = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (++inspected > MaxProbeFlowPeers * 4) break;
                    int playerId = ObjectInt(entry.Key, -1);
                    if (playerId < 0) playerId = ObjectInt(MemberByNames(entry.Value, "PlayerId", "PeerId", "Id"), -1);
                    ProbeFlowPeer peer = GetFlowPeer(data, playerId);
                    FillFlowPeer(peer, entry.Value, source, session);
                }
                return;
            }
            IEnumerable values = collection as IEnumerable;
            if (values == null)
            {
                FillFlowPeer(GetFlowPeer(data, ObjectInt(MemberByNames(collection, "PlayerId", "PeerId", "Id"), -1)), collection, source, session);
                return;
            }
            int count = 0;
            foreach (object value in values)
            {
                if (++count > MaxProbeFlowPeers * 4) break;
                int playerId = ObjectInt(MemberByNames(value, "PlayerId", "PeerId", "Id"), -1);
                FillFlowPeer(GetFlowPeer(data, playerId), value, source, session);
            }
        }

        private static ProbeFlowPeer GetFlowPeer(ProbeFlowState data, int playerId)
        {
            foreach (ProbeFlowPeer peer in data.peers)
                if (peer.playerId == playerId) return peer;
            if (data.peers.Count >= MaxProbeFlowPeers)
            {
                data.omittedPeerCount++;
                ProbeFlowPeer omitted = new ProbeFlowPeer { playerId = playerId };
                return omitted;
            }
            ProbeFlowPeer created = new ProbeFlowPeer { playerId = playerId };
            data.peers.Add(created);
            return created;
        }

        private static void FillFlowPeer(ProbeFlowPeer peer, object value, string source, Session session)
        {
            if (peer == null || value == null) return;
            if (value is bool)
            {
                if (source.IndexOf("barrier", StringComparison.OrdinalIgnoreCase) >= 0) peer.barrierReady = (bool)value;
                else { peer.ready = (bool)value; peer.worldReady = (bool)value; }
                peer.values.Add(new ProbeValue { name = source, type = "bool", value = value.ToString() });
                return;
            }
            peer.worldEpoch = ObjectInt64(MemberByNames(value, "WorldEpoch", "Epoch"), peer.worldEpoch);
            peer.transferId = BoundedText(MemberByNames(value, "TransferId", "TransactionId"), 128);
            peer.scene = BoundedText(MemberByNames(value, "Scene", "SceneName"), 256);
            peer.phase = BoundedText(MemberByNames(value, "Phase", "State", "Status"), 128);
            peer.ready = ObjectBool(MemberByNames(value, "Ready", "IsReady"), peer.ready);
            peer.worldReady = ObjectBool(MemberByNames(value, "WorldReady", "IsWorldReady", "Ready", "IsReady"), peer.worldReady);
            bool applying = ObjectBool(MemberByNames(value, "Applying", "Loading", "Transferring"), false);
            bool failed = ObjectBool(MemberByNames(value, "Failed", "FailedTransfer"), false);
            peer.barrierReady = ObjectBool(MemberByNames(value, "BarrierReady", "ReadyAtBarrier", "AtBarrier", "Barrier"), peer.worldReady && !applying);
            if (string.IsNullOrEmpty(peer.phase)) peer.phase = failed ? "Failed" : applying ? "Applying" : peer.worldReady ? "Ready" : "Waiting";
            if (session != null && peer.playerId >= 0 && string.IsNullOrEmpty(peer.name)) peer.name = BoundedText(session.NameOf(peer.playerId), 128);
            CaptureValues(peer.values, value, source, 1, new List<object>());
        }

        private static ProbeTimeState TimeData()
        {
            ProbeTimeState data = new ProbeTimeState();
            ProbeFeatureContext feature = ProbeFeatureContext.Create("PoECoop.Sync.TimeSync", "PoECoop.Sync.TimeStateSync");
            data.syncAvailable = feature.Available;
            data.instanceAvailable = feature.Instance != null;
            data.worldEpoch = feature.Int64(-1, "WorldEpoch", "Epoch", "CurrentWorldEpoch");
            data.stateSeq = feature.Int64(-1, "StateSeq", "StateSequence", "Sequence", "SnapshotSequence");
            data.guestStateSeq = feature.Int64(-1, "GuestSequence", "GuestStateSeq", "LastSequence");
            data.hasGuestSnapshot = feature.Boolean(false, "HasGuestSnapshot");
            data.barrierPaused = feature.Boolean(false, "BarrierPaused", "Barrier");
            data.turnLocked = feature.Boolean(false, "TurnLocked");
            CaptureFeatureValues(data.sync, feature, "time");
            CaptureFeatureValues(data.sync, ProbeFeatureContext.Create("PoECoop.Sync.PauseSync", "PoECoop.Sync.PauseStateSync"), "pause");
            try
            {
                TimeController controller = TimeController.Instance;
                if (controller != null)
                {
                    data.playerPaused = PrivateValue(controller, "m_PlayerPaused", false);
                    data.uiPaused = controller.UiPaused;
                    data.logicalPaused = data.playerPaused || data.uiPaused;
                    data.paused = controller.Paused;
                    data.prohibitPause = controller.ProhibitPause;
                    data.slow = controller.Slow;
                    data.fast = controller.Fast;
                    data.safeToPause = TimeController.IsSafeToPause;
                    data.requestedScale = PrivateValue(controller, "TimeScale", 1f);
                    data.normalScale = controller.NormalTime;
                    data.slowScale = controller.SlowTime;
                    data.fastScale = controller.FastTime;
                    data.overrideScale = controller.OverrideTime;
                    data.debugScale = controller.DebugTimeScaleMod;
                    data.resumeScale = PrivateValue(controller, "m_resumeTime", 1f);
                    data.realtimeSinceStartup = controller.RealtimeSinceStartupThisFrame;
                }
                data.effectiveScale = Time.timeScale;
                data.combatScale = GameState.Option != null ? GameState.Option.CombatSpeed : 1f;
                WorldTime world = WorldTime.Instance;
                if (world != null && world.CurrentTime != null)
                {
                    data.worldClock = BoundedText(world.GetTimeDisplayString(), 128);
                    data.worldDate = BoundedText(world.GetDateDisplayString(), 128);
                    data.worldYear = world.CurrentYear;
                    data.worldMonth = world.CurrentMonth;
                    data.worldDay = world.CurrentDay;
                    data.worldHour = world.CurrentHour;
                    data.worldMinute = world.CurrentMinute;
                    data.worldSecond = world.CurrentSecond;
                    data.frameWorldSeconds = world.FrameWorldSeconds;
                    data.gameSecondsPerRealSecond = world.GameSecondsPerRealSecond;
                    data.worldSecondsRemainder = PrivateValue(world, "SecondsTimer", 0f);
                    data.realWorldPlayTime = world.RealWorldPlayTime;
                }
            }
            catch (Exception ex)
            {
                data.error = ErrorText(ex);
            }
            return data;
        }

        private static ProbeDialogueState DialogueData()
        {
            ProbeDialogueState data = new ProbeDialogueState();
            ProbeFeatureContext feature = ProbeFeatureContext.Create("PoECoop.Sync.DialogueSync", "PoECoop.Sync.DialogueStateSync");
            data.syncAvailable = feature.Available;
            Session session = feature.Value("Session") as Session;
            data.worldEpoch = feature.Int64(-1, "WorldEpoch", "Epoch", "CurrentWorldEpoch");
            data.stateSeq = session != null && session.IsGuest
                ? feature.Int64(-1, "GuestSequence", "GuestStateSeq")
                : feature.Int64(-1, "StateSeq", "StateSequence", "Sequence", "SnapshotSequence");
            object syncActive = feature.Value("Active") ?? feature.Value("GuestLatest", "LastSnapshot");
            data.pickerPlayerId = ObjectInt(feature.Value("PickerPlayerId", "PickerPeerId", "PickerId", "Picker"), ObjectInt(MemberByNames(syncActive, "PickerPlayerId", "PickerId"), -1));
            data.picker = feature.Text("PickerName", "Picker");
            if (session != null && data.pickerPlayerId >= 0) data.picker = BoundedText(session.NameOf(data.pickerPlayerId), 128);
            data.syncConversationId = ObjectInt(MemberByNames(syncActive, "ConversationId", "Id"), -1);
            data.syncNodeId = ObjectInt(MemberByNames(syncActive, "CurrentNodeId", "CurrentNodeID", "NodeId"), ObjectInt(MemberByNames(MemberByNames(syncActive, "Player"), "CurrentNodeID", "CurrentNodeId"), -1));
            data.nextNodeId = ObjectInt(MemberByNames(syncActive, "NextNodeId", "NextNodeID"), -1);
            CaptureFeatureValues(data.sync, feature, "dialogue");
            try
            {
                ConversationManager manager = ConversationManager.Instance;
                IEnumerable players = manager != null ? PrivateObject(manager, "ActiveConversationPlayers") as IEnumerable : null;
                List<FlowChartPlayer> activePlayers = new List<FlowChartPlayer>();
                if (players != null)
                {
                    foreach (object value in players)
                    {
                        FlowChartPlayer player = value as FlowChartPlayer;
                        if (player == null) continue;
                        data.activeCount++;
                        if (activePlayers.Count < MaxProbeDialogueConversations) activePlayers.Add(player);
                    }
                }
                UIConversationManager ui = UIConversationManager.Instance;
                FlowChartPlayer active = ui != null ? PrivateObject(ui, "m_ActiveFlowChart") as FlowChartPlayer : null;
                if (active == null && manager != null) active = manager.GetActiveConversationForHUD();
                if (active == null && activePlayers.Count > 0) active = activePlayers[0];
                for (int i = 0; i < activePlayers.Count; i++)
                {
                    ProbeDialogueConversation conversation = DialogueConversationData(activePlayers[i], i);
                    if (ReferenceEquals(activePlayers[i], active)) data.activeId = conversation.id;
                    data.conversations.Add(conversation);
                }
                data.omittedConversationCount = Math.Max(0, data.activeCount - activePlayers.Count);
                data.active = DialogueConversationData(active, activePlayers.IndexOf(active));
                if (string.IsNullOrEmpty(data.activeId) && data.active != null) data.activeId = data.active.id;
                if (active != null)
                {
                    Conversation conversation = active.CurrentFlowChart as Conversation;
                    if (conversation != null)
                    {
                        data.file = BoundedText(conversation.Filename, 512);
                        data.nodeText = BoundedText(conversation.GetActiveNodeText(active), 16384);
                        IList responses = conversation.GetResponseNodes(active);
                        IList qualified = conversation.GetResponseNodes(active, true);
                        data.showResponses = qualified.Count > 0;
                        data.responseCount = responses.Count;
                        int selected = ui != null ? PrivateValue(ui, "m_ControllerSelection", -1) : -1;
                        int outstanding = ui != null ? PrivateValue(ui, "m_OutstandingResponses", responses.Count) : responses.Count;
                        UIConsoleText textList = DialogueTextList(ui, active);
                        int paragraphCount = textList != null ? textList.ParagraphCount : 0;
                        for (int i = 0; i < responses.Count && i < MaxProbeDialogueResponses; i++)
                        {
                            object response = responses[i];
                            FlowChartNode node = response as FlowChartNode;
                            int paragraph = i - outstanding + paragraphCount;
                            string text = node != null ? conversation.GetNodeText(active, node) : "";
                            if (textList != null && paragraph >= 0 && paragraph < paragraphCount)
                                text = textList.GetParagraph(paragraph);
                            data.responses.Add(new ProbeDialogueResponse
                            {
                                index = i,
                                nodeId = node != null ? node.NodeID : -1,
                                paragraph = paragraph,
                                text = BoundedText(text, 4096),
                                qualified = qualified.Contains(response),
                                selected = i == selected,
                                clientScreen = DialogueResponseScreen(textList, paragraph)
                            });
                        }
                        data.omittedResponseCount = Math.Max(0, responses.Count - data.responses.Count);
                    }
                }
                if (ui != null)
                {
                    data.visible = ui.WindowActive();
                    data.conversationVisible = ui.ConversationWindow != null && ui.ConversationWindow.activeInHierarchy;
                    data.interactionVisible = ui.InteractionWindow != null && ui.InteractionWindow.activeInHierarchy;
                    UIConsoleText textList = DialogueTextList(ui, active);
                    data.visibleText = textList != null && textList.textLabel != null ? BoundedText(textList.textLabel.text, 16384) : "";
                    if (ui.ContinueButton != null)
                        data.continueButton = TacticalButtonData("dialogueContinue", ui, ui.ContinueButton, null, null);
                }
            }
            catch (Exception ex)
            {
                data.error = ErrorText(ex);
            }
            return data;
        }

        private static ProbeDialogueConversation DialogueConversationData(FlowChartPlayer player, int index)
        {
            if (player == null) return null;
            string file = player.CurrentFlowChart != null ? BoundedText(player.CurrentFlowChart.Filename, 512) : "";
            return new ProbeDialogueConversation
            {
                id = file + "#" + index,
                file = file,
                ownerId = NetworkEntityId(player.OwnerObject),
                ownerInstanceId = SavedInstanceId(player.OwnerObject),
                displayMode = player.FlowChartDisplayMode.ToString(),
                startNode = player.StartNodeID,
                currentNode = player.CurrentNodeID,
                timer = player.Timer,
                completed = player.Completed,
                disableVO = player.DisableVO
            };
        }

        private static UIConsoleText DialogueTextList(UIConversationManager ui, FlowChartPlayer player)
        {
            if (ui == null) return null;
            return player != null && player.FlowChartDisplayMode == FlowChartPlayer.DisplayMode.Interaction ? ui.InteractionTextList : ui.ConversationTextList;
        }

        private static ProbeScreen DialogueResponseScreen(UIConsoleText textList, int paragraph)
        {
            if (textList == null || textList.textLabel == null || paragraph < 0 || paragraph >= textList.ParagraphCount) return null;
            try
            {
                IEnumerable paragraphs = PrivateObject(textList, "mParagraphs") as IEnumerable;
                if (paragraphs == null) return null;
                int index = 0, before = 0, after = 0, width = 0, lines = 0;
                foreach (object value in paragraphs)
                {
                    int lineCount = PrivateValue(value, "lines", new string[0]).Length;
                    if (index == paragraph)
                    {
                        width = PrivateValue(value, "maxLineWidth", 0);
                        lines = lineCount;
                    }
                    else if (index < paragraph) before += lineCount;
                    else after += lineCount;
                    index++;
                }
                if (lines == 0) return null;
                float scroll = PrivateValue(textList, "mScroll", 0f);
                float lineOffset = (textList.style == UITextList.Style.Chat ? before : after) + lines * 0.5f - scroll;
                float y = lineOffset * textList.LineHeight;
                if (textList.textLabel.pivot == UIWidget.Pivot.Top || textList.textLabel.pivot == UIWidget.Pivot.TopLeft || textList.textLabel.pivot == UIWidget.Pivot.TopRight) y = -y;
                Vector3 world = textList.transform.localToWorldMatrix.MultiplyPoint3x4(new Vector3(width * 0.5f, y, 0f));
                Camera camera = InGameUILayout.NGUICamera;
                return camera != null ? ScreenPoint(camera, world) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static ProbeAreaState AreaData(Camera camera)
        {
            ProbeAreaState data = new ProbeAreaState();
            ProbeFeatureContext feature = ProbeFeatureContext.Create("PoECoop.Sync.AreaTransitionSync", "PoECoop.Sync.AreaSync", "PoECoop.Sync.AreaStateSync");
            data.syncAvailable = feature.Available;
            data.worldEpoch = feature.Int64(-1, "WorldEpoch", "Epoch", "CurrentWorldEpoch", "ObservedEpoch");
            data.stateSeq = feature.Int64(-1, "StateSeq", "StateSequence", "Sequence", "SnapshotSequence");
            data.state = feature.Text("State", "Phase", "TransitionState");
            data.loading = GameState.IsLoading;
            object pending = feature.Value("Pending", "PendingRequest", "CurrentTransition");
            data.pendingKind = BoundedText(MemberByNames(pending, "Kind", "Type"), 128);
            data.pendingRequestId = ObjectInt(MemberByNames(pending, "Id", "RequestId"), -1);
            if (string.IsNullOrEmpty(data.state)) data.state = pending != null ? "Pending" : data.loading ? "Loading" : "Idle";
            CaptureFeatureValues(data.sync, feature, "area");
            try
            {
                data.loading = GameState.IsLoading;
                data.currentScene = GameState.Instance != null && GameState.Instance.CurrentMap != null ? BoundedText(GameState.Instance.CurrentMap.SceneName, 256) : "";
                data.nextScene = GameState.Instance != null && GameState.Instance.CurrentNextMap != null ? BoundedText(GameState.Instance.CurrentNextMap.SceneName, 256) : "";
                if (GameState.s_playerCharacter != null)
                {
                    data.requestedStartPoint = GameState.s_playerCharacter.StartPointLink.ToString();
                    data.requestedStartPointName = BoundedText(GameState.s_playerCharacter.StartPointName, 256);
                }
                if (FadeManager.Instance != null)
                {
                    data.fadeActive = FadeManager.Instance.IsFadeActive();
                    data.fadeValue = FadeManager.Instance.FadeValue;
                }
                List<SceneTransition> transitions = SceneTransition.s_activeSceneTransitionsList;
                if (transitions != null)
                {
                    data.transitionCount = transitions.Count;
                    for (int i = 0; i < transitions.Count && data.transitions.Count < MaxProbeAreaTransitions; i++)
                    {
                        SceneTransition transition = transitions[i];
                        if (transition == null) continue;
                        IList waiting = PrivateObject(transition, "m_partyWaitList") as IList;
                        data.transitions.Add(new ProbeAreaTransition
                        {
                            id = SavedInstanceId(transition.gameObject),
                            name = BoundedText(transition.name, 256),
                            linkedScene = transition.LinkedScene.ToString(),
                            linkedMapTag = BoundedText(transition.LinkedMapTag, 256),
                            startPoint = transition.StartPointLink.ToString(),
                            startPointOverride = BoundedText(transition.StartPointOverride, 256),
                            active = PrivateValue(transition, "m_isActive", false),
                            waiting = waiting != null && waiting.Count > 0,
                            waitingCount = waiting != null ? waiting.Count : 0,
                            usable = transition.IsUsable,
                            sameMap = transition.SameMapTransitionPoint != null,
                            world = VectorData(transition.transform.position),
                            screen = ScreenPoint(camera, transition.transform.position)
                        });
                    }
                    data.omittedTransitionCount = Math.Max(0, transitions.Count - data.transitions.Count);
                }
                StartPoint[] startPoints = UnityEngine.Object.FindObjectsOfType<StartPoint>();
                data.startPointCount = startPoints.Length;
                foreach (StartPoint startPoint in startPoints)
                {
                    if (startPoint == null || data.startPoints.Count >= MaxProbeAreaStartPoints) continue;
                    data.startPoints.Add(new ProbeAreaStartPoint
                    {
                        id = SavedInstanceId(startPoint.gameObject),
                        name = BoundedText(startPoint.name, 256),
                        location = startPoint.Location.ToString(),
                        chosen = StartPoint.s_ChosenStartPoint == startPoint,
                        world = VectorData(startPoint.transform.position),
                        screen = ScreenPoint(camera, startPoint.transform.position)
                    });
                }
                data.omittedStartPointCount = Math.Max(0, startPoints.Length - data.startPoints.Count);
            }
            catch (Exception ex)
            {
                data.error = ErrorText(ex);
            }
            return data;
        }

        private static ProbeRestState RestData()
        {
            ProbeRestState data = new ProbeRestState();
            ProbeFeatureContext feature = ProbeFeatureContext.Create("PoECoop.Sync.RestSync", "PoECoop.Sync.RestStateSync");
            data.syncAvailable = feature.Available;
            data.worldEpoch = feature.Int64(-1, "WorldEpoch", "Epoch", "CurrentWorldEpoch");
            data.stateSeq = feature.Int64(-1, "StateSeq", "StateSequence", "CurrentStateSequence", "Sequence", "SnapshotSequence");
            object syncRest = feature.Value("Transaction") ?? feature.Value("Snapshot", "GuestSnapshot");
            data.transactionId = BoundedText(MemberByNames(syncRest, "TransactionId", "RestTransactionId", "Transaction", "Id"), 128);
            data.transactionRevision = ObjectInt(MemberByNames(syncRest, "Revision", "TransactionRevision"), -1);
            data.initiatorPlayerId = ObjectInt(MemberByNames(syncRest, "Initiator", "InitiatorPlayerId"), -1);
            data.roomIndex = ObjectInt(MemberByNames(syncRest, "RoomIndex"), -1);
            data.sourceId = BoundedText(MemberByNames(syncRest, "SourceId"), 128);
            data.phase = BoundedText(MemberByNames(syncRest, "Phase", "State", "RestPhase"), 128);
            data.committed = ObjectBool(MemberByNames(syncRest, "Committed"), false);
            data.startTime = ObjectInt(MemberByNames(syncRest, "StartTime"), -1);
            data.endTime = ObjectInt(MemberByNames(syncRest, "EndTime"), -1);
            data.advancedSeconds = ObjectInt(MemberByNames(syncRest, "AdvancedSeconds"), 0);
            CaptureFeatureValues(data.sync, feature, "rest");
            try
            {
                data.partyInRestZone = RestZone.PartyInRestZone;
                RestZone.CannotCampReason reason = RestZone.WhyCannotCamp();
                data.cannotCampReason = reason.ToString();
                data.canCamp = reason == 0;
                if (GameState.s_playerCharacter != null)
                {
                    PlayerInventory inventory = GameState.s_playerCharacter.Inventory as PlayerInventory;
                    if (inventory != null)
                    {
                        data.supplies = inventory.CampingSuppliesTotal;
                        data.maxSupplies = CampingSupplies.StackMaximum;
                        data.currency = inventory.currencyTotalValue.v;
                    }
                }
                foreach (PartyMemberAI member in PartyMemberAI.PartyMembers)
                {
                    if (member == null) continue;
                    CharacterStats stats = member.GetComponent<CharacterStats>();
                    if (stats == null) continue;
                    data.party.Add(new ProbeRestPartyMember
                    {
                        actorId = NetworkEntityId(member.gameObject),
                        name = BoundedText(CharacterStats.Name(member), 256),
                        fatigue = stats.CurrentFatigueLevel.ToString(),
                        survival = stats.CalculateSkill(CharacterStats.SkillType.Survival),
                        survivalBonus = stats.LastSelectedSurvivalBonus,
                        survivalSubBonus = stats.LastSelectedSurvivalSubBonus
                    });
                }
                UIRestConfirmBox confirm = UIRestConfirmBox.Instance;
                if (confirm != null)
                {
                    data.visible = confirm.WindowActive();
                    data.mode = PrivateObject(confirm, "currentType").ToString();
                    if (confirm.RestButton != null) data.restButton = TacticalButtonData("rest", confirm, confirm.RestButton, null, null);
                    if (confirm.StashButton != null) data.stashButton = TacticalButtonData("restStash", confirm, confirm.StashButton, null, null);
                    if (confirm.CancelButton != null) data.cancelButton = TacticalButtonData("restCancel", confirm, confirm.CancelButton, null, null);
                }
                UIRestMovieManager movie = UIRestMovieManager.Instance;
                if (movie != null)
                {
                    data.movieVisible = movie.WindowActive();
                    data.movieMode = movie.ForMode.ToString();
                    data.moviePlaying = movie.MovieMan != null && movie.MovieMan.IsMoviePlaying();
                    if (string.IsNullOrEmpty(data.mode) || data.movieVisible) data.mode = data.movieMode;
                }
                if (string.IsNullOrEmpty(data.mode)) data.mode = BoundedText(MemberByNames(syncRest, "Mode"), 128);
                if (string.IsNullOrEmpty(data.movieMode)) data.movieMode = BoundedText(MemberByNames(syncRest, "Movie", "RequestedMovie"), 128);
                if (string.IsNullOrEmpty(data.phase)) data.phase = data.movieVisible ? "Movie" : data.visible ? "Confirm" : "None";
            }
            catch (Exception ex)
            {
                data.error = ErrorText(ex);
            }
            return data;
        }

        private static ProbeLootState LootData()
        {
            ProbeLootState data = new ProbeLootState();
            ProbeFeatureContext feature = ProbeFeatureContext.Create("PoECoop.Sync.LootSync", "PoECoop.Sync.LootStateSync");
            data.syncAvailable = feature.Available;
            data.worldEpoch = feature.Int64(-1, "WorldEpoch", "Epoch", "CurrentWorldEpoch");
            data.stateSeq = feature.Int64(-1, "StateSeq", "StateSequence", "Sequence", "SnapshotSequence", "GuestSequence");
            data.revision = feature.Int64(-1, "Revision", "LootRevision", "StateRevision", "GuestRevision", "GuestShownRevision");
            data.sessionCount = ObjectInt(feature.Value("SessionCount", "SessionsCount", "ActiveSessionCount"), ObjectCount(feature.Value("Sessions", "ActiveSessions")));
            data.viewerCount = ObjectInt(feature.Value("ViewerCount", "ViewersCount"), LootViewerCount(feature));
            data.activeSessionId = feature.Text("ActiveSessionId", "SessionId", "CurrentSessionId", "GuestSession", "GuestShownSession");
            CaptureFeatureValues(data.sync, feature, "loot");
            try
            {
                UILootManager loot = UILootManager.Instance;
                if (loot == null) return data;
                data.visible = loot.WindowActive();
                data.selectedStash = loot.SelectedStash;
                Container container = PrivateObject(loot, "m_Container") as Container;
                data.containerId = container != null ? SavedInstanceId(container.gameObject) : "";
                data.selectedActorId = loot.SelectedCharacter != null ? NetworkEntityId(loot.SelectedCharacter.gameObject) : "";
                List<BaseInventory> seen = new List<BaseInventory>();
                if (container != null) AddLootContainer(data, container.GetComponent<Inventory>(), "container", seen);
                BaseInventory displayed = loot.ItemGrid != null ? loot.ItemGrid.Inventory : null;
                CombinedInventory combined = displayed as CombinedInventory;
                if (combined != null)
                {
                    foreach (Inventory inventory in combined.CombinedInventories)
                        AddLootContainer(data, inventory, "areaContainer", seen);
                }
                AddLootContainer(data, displayed, combined != null ? "combined" : "displayed", seen);
                data.containerCount = data.containers.Count;
                if (loot.TakeAllButton != null) data.takeAllButton = TacticalButtonData("lootTakeAll", loot, loot.TakeAllButton, null, null);
                if (loot.CloseButton != null) data.closeButton = UiControlData("lootClose", loot.CloseButton.transform);
            }
            catch (Exception ex)
            {
                data.error = ErrorText(ex);
            }
            return data;
        }

        private static int LootViewerCount(ProbeFeatureContext feature)
        {
            object groups = feature.Value("Groups", "LootGroups");
            IDictionary dictionary = groups as IDictionary;
            if (dictionary == null) return feature.Boolean(false, "GuestOpen") ? 1 : 0;
            int count = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                count += ObjectCount(MemberByNames(entry.Value, "SessionIds", "Sessions", "ViewerIds"));
                if (ObjectBool(MemberByNames(entry.Value, "HostViewer"), false)) count++;
            }
            return count;
        }

        private static void AddLootContainer(ProbeLootState data, BaseInventory inventory, string kind, List<BaseInventory> seen)
        {
            if (inventory == null || seen.Contains(inventory)) return;
            seen.Add(inventory);
            if (data.containers.Count >= MaxProbeInventories)
            {
                data.omittedContainerCount++;
                return;
            }
            data.containers.Add(InventoryManifest(inventory, kind));
        }

        private static ProbeInventoryState InventoryData()
        {
            ProbeInventoryState data = new ProbeInventoryState();
            ProbeFeatureContext feature = ProbeFeatureContext.Create("PoECoop.Sync.InventorySync", "PoECoop.Sync.InventoryStateSync");
            ProbeFeatureContext equipmentFeature = ProbeFeatureContext.Create("PoECoop.Sync.EquipmentSync", "PoECoop.Sync.EquipmentStateSync");
            data.syncAvailable = feature.Available || equipmentFeature.Available;
            data.worldEpoch = feature.Int64(equipmentFeature.Int64(-1, "WorldEpoch", "Epoch", "CurrentWorldEpoch"), "WorldEpoch", "Epoch", "CurrentWorldEpoch");
            data.stateSeq = feature.Int64(equipmentFeature.Int64(-1, "StateSeq", "StateSequence", "CurrentStateSequence", "Sequence", "SnapshotSequence"), "StateSeq", "StateSequence", "CurrentStateSequence", "Sequence", "SnapshotSequence");
            data.revision = feature.Int64(equipmentFeature.Int64(-1, "Revision", "EquipmentRevision", "StateRevision"), "Revision", "InventoryRevision", "StateRevision");
            CaptureFeatureValues(data.sync, feature, "inventory");
            CaptureFeatureValues(data.sync, equipmentFeature, "equipment");
            try
            {
                UIInventoryManager ui = UIInventoryManager.Instance;
                if (ui != null)
                {
                    data.visible = ui.WindowActive();
                    data.stashVisible = ui.IsStashOpen;
                    data.dragging = ui.DraggingItem;
                    data.selectedActorId = ui.SelectedCharacter != null ? NetworkEntityId(ui.SelectedCharacter.gameObject) : "";
                    data.draggedItemId = ui.DraggedItem != null ? NetworkItemId(ui.DraggedItem.BaseItem) : "";
                }
                List<BaseInventory> seen = new List<BaseInventory>();
                foreach (PartyMemberAI member in PartyMemberAI.PartyMembers)
                {
                    if (member == null) continue;
                    AddInventoryManifest(data, member.Inventory, "party", seen);
                    AddInventoryManifest(data, member.GetComponent<QuickbarInventory>(), "quickbar", seen);
                    AddEquipmentManifest(data, member.GetComponent<Equipment>());
                }
                if (GameState.s_playerCharacter != null)
                {
                    PlayerInventory player = GameState.s_playerCharacter.Inventory as PlayerInventory;
                    if (player != null)
                    {
                        AddInventoryManifest(data, GameState.PlayerStashInventory, "stash", seen);
                        AddInventoryManifest(data, player.QuestInventory, "quest", seen);
                        AddInventoryManifest(data, player.CraftingInventory, "crafting", seen);
                    }
                }
                data.inventoryCount = data.inventories.Count;
                data.equipmentCount = data.equipment.Count;
                UIInventoryGridItem[] controls = UnityEngine.Object.FindObjectsOfType<UIInventoryGridItem>();
                foreach (UIInventoryGridItem control in controls)
                {
                    if (control == null || !control.gameObject.activeInHierarchy) continue;
                    data.itemControlCount++;
                    if (data.itemControls.Count >= MaxProbeItemControls) continue;
                    ProbeUiControl controlData = UiControlData("inventoryItem", control);
                    controlData.slot = control.Slot;
                    controlData.blocked = control.Blocked;
                    controlData.empty = control.Empty;
                    controlData.equipped = control.IsEquipped;
                    controlData.equipmentSlot = control.EquipmentSlot.ToString();
                    if (control.InvItem != null)
                    {
                        controlData.itemId = NetworkItemId(control.InvItem.BaseItem);
                        controlData.text = control.InvItem.BaseItem != null ? BoundedText(control.InvItem.BaseItem.Name, 512) : "";
                    }
                    if (control.Owner != null)
                    {
                        controlData.inventoryId = NetworkInventoryId(control.Owner.Inventory);
                        controlData.ownerId = control.Owner.OwnerGameObject != null ? NetworkEntityId(control.Owner.OwnerGameObject) : "";
                    }
                    data.itemControls.Add(controlData);
                }
                data.omittedItemControlCount = Math.Max(0, data.itemControlCount - data.itemControls.Count);
            }
            catch (Exception ex)
            {
                data.error = ErrorText(ex);
            }
            return data;
        }

        private static void AddInventoryManifest(ProbeInventoryState data, BaseInventory inventory, string kind, List<BaseInventory> seen)
        {
            if (inventory == null || seen.Contains(inventory)) return;
            seen.Add(inventory);
            if (data.inventories.Count >= MaxProbeInventories) return;
            ProbeInventory manifest = InventoryManifest(inventory, kind);
            data.itemCount += manifest.itemCount;
            data.inventories.Add(manifest);
        }

        private static ProbeInventory InventoryManifest(BaseInventory inventory, string kind)
        {
            ProbeInventory data = new ProbeInventory
            {
                kind = kind,
                inventoryId = NetworkInventoryId(inventory),
                ownerId = NetworkEntityId(inventory.gameObject),
                ownerInstanceId = SavedInstanceId(inventory.gameObject),
                ownerName = BoundedText(inventory.AttachedCharacter != null ? CharacterStats.Name(inventory.AttachedCharacter) : inventory.name, 256),
                maxItems = inventory.MaxItems,
                itemCount = inventory.ItemList != null ? inventory.ItemList.Count : 0
            };
            if (inventory.ItemList == null) return data;
            foreach (InventoryItem item in inventory.ItemList)
            {
                if (data.items.Count >= MaxProbeItemsPerInventory) break;
                if (item == null) continue;
                data.items.Add(ItemData(item.BaseItem, item.StackSize, item.uiSlot, "", false, item.Original, item.AreaLootSource));
            }
            data.omittedItemCount = Math.Max(0, inventory.ItemList.Count - data.items.Count);
            return data;
        }

        private static void AddEquipmentManifest(ProbeInventoryState data, Equipment equipment)
        {
            if (equipment == null || equipment.CurrentItems == null) return;
            EquipmentSet current = equipment.CurrentItems;
            ProbeEquipment manifest = new ProbeEquipment
            {
                ownerId = NetworkEntityId(equipment.gameObject),
                ownerInstanceId = SavedInstanceId(equipment.gameObject),
                ownerName = BoundedText(CharacterStats.Name(equipment), 256),
                selectedWeaponSet = current.SelectedWeaponSet
            };
            Equippable[] slots = current.SerializedEquipment;
            string[] names = { "Head", "Neck", "Armor", "Hands", "RightRing", "LeftRing", "Cape_DEPRECATED", "Feet", "Waist", "Grimoire", "Pet" };
            for (int i = 0; i < slots.Length && i < names.Length; i++) AddEquipmentItem(manifest, slots[i], names[i]);
            if (current.AlternateWeaponSets != null)
            {
                for (int i = 0; i < current.AlternateWeaponSets.Length; i++)
                {
                    WeaponSet set = current.AlternateWeaponSets[i];
                    if (set == null) continue;
                    string suffix = i == 0 ? "" : (i + 1).ToString();
                    AddEquipmentItem(manifest, set.PrimaryWeapon, "PrimaryWeapon" + suffix);
                    AddEquipmentItem(manifest, set.SecondaryWeapon, "SecondaryWeapon" + suffix);
                }
            }
            manifest.slotCount = manifest.items.Count;
            data.equipment.Add(manifest);
        }

        private static void AddEquipmentItem(ProbeEquipment manifest, Equippable item, string slot)
        {
            if (item == null) return;
            manifest.items.Add(ItemData(item, 1, -1, slot, true, false, false));
        }

        private static ProbeItem ItemData(Item item, int stack, int slot, string equipmentSlot, bool equipped, bool original, bool areaLootSource)
        {
            if (item == null) return new ProbeItem { stack = stack, slot = slot, equipmentSlot = equipmentSlot, equipped = equipped };
            ProbeItem data = new ProbeItem
            {
                itemId = NetworkItemId(item),
                prefabItemId = item.Prefab != null ? SavedInstanceId(item.Prefab.gameObject) : "",
                prefabName = item.Prefab != null ? BoundedText(item.Prefab.name.Replace("(Clone)", ""), 256) : "",
                name = BoundedText(item.name.Replace("(Clone)", ""), 256),
                displayName = BoundedText(item.Name, 512),
                type = item.GetType().Name,
                filter = item.FilterType.ToString(),
                displayStringId = item.DisplayName != null ? item.DisplayName.StringID : -1,
                descriptionStringId = item.DescriptionText != null ? item.DescriptionText.StringID : -1,
                stack = stack,
                slot = slot,
                maxStack = item.MaxStackSize,
                original = original,
                areaLootSource = areaLootSource,
                equipped = equipped,
                equipmentSlot = equipmentSlot,
                quest = item.IsQuestItem,
                ingredient = item.IsIngredient,
                unique = item.Unique,
                canSell = item.CanSell(),
                value = item.GetValue()
            };
            try
            {
                Equippable equippable = item as Equippable;
                if (equippable != null)
                {
                    if (string.IsNullOrEmpty(data.equipmentSlot)) data.equipmentSlot = equippable.EquippedSlot.ToString();
                    foreach (ItemModComponent component in equippable.AttachedItemMods)
                    {
                        if (component == null || component.Mod == null) continue;
                        if (data.mods.Count < MaxProbeItemMods)
                        {
                            ItemMod mod = component.Mod;
                            data.mods.Add(new ProbeItemMod
                            {
                                name = BoundedText(mod.name.Replace("(Clone)", ""), 256),
                                displayName = BoundedText(mod.DisplayName.GetText(), 512),
                                displayStringId = mod.DisplayName != null ? mod.DisplayName.StringID : -1,
                                category = mod.ModEnchantCategory.ToString(),
                                cost = mod.Cost,
                                cursesItem = mod.CursesItem
                            });
                        }
                    }
                    data.omittedModCount = Math.Max(0, equippable.AttachedItemMods.Count - data.mods.Count);
                }
                Consumable consumable = item as Consumable;
                if (consumable != null)
                {
                    data.consumable = true;
                    data.consumableType = consumable.Type.ToString();
                    data.usageType = consumable.UsageType.ToString();
                    data.usageCount = consumable.UsageCount;
                    data.usesRemaining = consumable.UsesRemaining;
                    data.consumableLevel = consumable.Level;
                }
                Grimoire grimoire = item.GetComponent<Grimoire>();
                if (grimoire != null)
                {
                    data.grimoire = true;
                    data.grimoireOwner = BoundedText(grimoire.PrimaryOwnerName, 256);
                    if (grimoire.Spells != null)
                    {
                        for (int level = 0; level < grimoire.Spells.Length; level++)
                        {
                            Grimoire.SpellChapter chapter = grimoire.Spells[level];
                            if (chapter == null || chapter.SpellData == null) continue;
                            for (int spellSlot = 0; spellSlot < chapter.SpellData.Length; spellSlot++)
                            {
                                GenericSpell spell = chapter.SpellData[spellSlot];
                                if (spell == null) continue;
                                if (data.grimoireSpells.Count < Grimoire.MaxSpellLevel * Grimoire.MaxSpellsPerLevel)
                                    data.grimoireSpells.Add((level + 1) + ":" + spellSlot + ":" + BoundedText(spell.name.Replace("(Clone)", ""), 256));
                                else data.omittedGrimoireSpellCount++;
                            }
                        }
                    }
                }
                EquipmentSoulbind soulbind = item.GetComponent<EquipmentSoulbind>();
                if (soulbind != null)
                {
                    data.hasSoulbind = true;
                    data.soulbound = soulbind.IsBound;
                    data.soulboundTo = soulbind.BoundGuid.ToString();
                    data.soulboundOwner = BoundedText(soulbind.CachedBoundOwnerOverrideName, 256);
                    if (string.IsNullOrEmpty(data.soulboundOwner) && soulbind.CachedBoundOwnerName != null)
                        data.soulboundOwner = BoundedText(soulbind.CachedBoundOwnerName.GetText(), 256);
                    data.soulboundClass = soulbind.BoundClass.ToString();
                    data.cannotUnbind = soulbind.CannotUnbind;
                    data.soulbindUnlockLevel = soulbind.UnlockLevel;
                    data.soulbindUnlockCount = soulbind.Unlocks != null ? soulbind.Unlocks.Length : 0;
                    data.soulbindProgress = soulbind.UnlockProgress;
                    data.soulbindDegenerateProgress = soulbind.DegenerateUnlockProgress;
                    data.unlocksComplete = soulbind.AreUnlocksComplete;
                }
            }
            catch (Exception ex)
            {
                data.error = ErrorText(ex);
            }
            return data;
        }

        private static ProbeUiControl UiControlData(string kind, Component control)
        {
            ProbeUiControl data = new ProbeUiControl { kind = kind, name = control != null ? BoundedText(control.name, 256) : "" };
            if (control == null) return data;
            try
            {
                data.active = control.gameObject.activeInHierarchy;
                UIIsButton button = control as UIIsButton;
                if (button != null) data.enabled = button.enabled;
                UIMultiSpriteImageButton multi = control as UIMultiSpriteImageButton;
                if (multi != null)
                {
                    data.enabled = multi.enabled;
                    data.text = multi.Label != null ? BoundedText(multi.Label.text, 512) : "";
                }
                data.clientScreen = ControlScreen(control);
            }
            catch (Exception ex)
            {
                data.error = ErrorText(ex);
            }
            return data;
        }

        private const int MaxProbeTacticalEvents = 512;
        private const int MaxProbeTacticalLocks = 64;
        private const int MaxProbeTacticalCounters = 128;
        private const int MaxProbeFreeActions = 128;

        private static ProbeTacticalState TacticalData()
        {
            ProbeTacticalState data = new ProbeTacticalState();
            TacticalSyncContext sync = TacticalSyncContext.Create();
            data.epoch = sync.Epoch;
            data.sequence = sync.Sequence;
            data.currentEventId = sync.CurrentEventId;
            data.full = sync.Full;
            data.fullKnown = sync.FullKnown;
            data.fullSource = sync.FullSource;
            data.pendingReason = sync.PendingReason;
            data.pendingPhase = sync.PendingPhase;
            data.pendingEpoch = sync.PendingEpoch;
            data.pendingSequence = sync.PendingSequence;
            data.applyingHostState = sync.ApplyingHostState;
            data.syncMetadataAvailable = sync.MetadataAvailable;
            data.syncInstanceAvailable = sync.Instance != null;
            data.blockedGuestMutators = BlockedGuestTacticalCounters();
            try
            {
                data.mode = GameState.Option.TacticalMode.ToString();
                data.enabled = TacticalModeManager.IsTacticalModeEnabled;
                data.shouldBeInCombat = GameState.ShouldBeInTacticalCombat;
                data.inCombat = TacticalModeManager.IsInTacticalCombat();
            }
            catch (Exception ex)
            {
                data.error = ErrorText(ex);
            }

            TacticalModeManager manager = TacticalModeManager.Instance;
            if (manager != null)
            {
                try
                {
                    data.settings = new ProbeTacticalSettings
                    {
                        effectTimePerRound = TacticalModeSettingsComponent.EffectTimePerRound,
                        immediateTimeThreshold = TacticalModeSettingsComponent.ImmediateTimeThreshold,
                        enterCombatBaseRecovery = TacticalModeSettingsComponent.EnterCombatBaseRecovery,
                        delayTurnStep = TacticalModeSettingsComponent.DelayTurnStep,
                        nextTurnStep = TacticalModeSettingsComponent.NextTurnStep,
                        movementTimePerRound = TacticalModeSettingsComponent.MovementTimePerRound,
                        baseRecovery = TacticalModeSettingsComponent.BaseRecovery,
                        postBaseRecovery = TacticalModeSettingsComponent.PostBaseRecovery,
                        baseRecoveryMultiplier = TacticalModeSettingsComponent.BaseRecoveryMultiplier,
                        reloadMultiplier = TacticalModeSettingsComponent.ReloadMultiplier,
                        visibleRounds = TacticalModeSettingsComponent.VisibleRounds,
                        weakestInterrupt = TacticalModeSettingsComponent.WeakestTacticalInterupt,
                        weakerInterrupt = TacticalModeSettingsComponent.WeakerTacticalInterupt,
                        weakInterrupt = TacticalModeSettingsComponent.WeakTacticalInterupt,
                        averageInterrupt = TacticalModeSettingsComponent.AverageTacticalInterupt,
                        strongInterrupt = TacticalModeSettingsComponent.StrongTacticalInterupt,
                        strongerInterrupt = TacticalModeSettingsComponent.StrongerTacticalInterupt,
                        strongestInterrupt = TacticalModeSettingsComponent.StrongestTacticalInterupt,
                        intellectDurationBonus = TacticalModeSettingsComponent.TacticalIntellectDurationBonus,
                        dualWieldingMultiplier = TacticalModeSettingsComponent.TacticalDualWieldingMult,
                        unlimitedMovement = manager.UnlimitedMovement,
                        unlimitedActions = manager.UnlimitedActions,
                        autoPassTurns = manager.AutoPassTurns
                    };
                    data.effectTime = manager.EffectTime;
                    data.currentEventTimer = PrivateValue(manager, "m_currentEventTimer", 0f);
                    data.currentEventRealTimer = manager.CurrentEventRealTimer;
                    data.spinUpTimer = PrivateValue(manager, "m_spinUpTimer", 0f);
                    data.spinDownComplete = PrivateValue(manager, "m_spinDownComplete", true);
                    data.turnLocked = manager.TurnLocked;
                    data.refreshCurrentEventLocked = PrivateValue(manager, "m_refreshCurrentEventLocked", false);
                    data.hasRoundStart = PrivateValue(manager, "m_hasRoundStart", false);
                    data.partyInCombat = PrivateValue(manager, "m_isPartyMemberInCombat", false);
                    data.isPlayerTurn = manager.IsPlayerTurn;
                    data.phase = TacticalPhase(manager.CurrentEvent, data.spinDownComplete, data.spinUpTimer, sync.IsGuest);
                    data.phaseSource = sync.IsGuest ? "guestNativeTimers" : "nativeTimers";
                    data.currentActor = TacticalActorData(manager.CurrentActor);
                    data.whoseTurn = TacticalActorData(manager.WhoseTurn);
                    data.currentActorIsWhoseTurn = manager.CurrentActor == manager.WhoseTurn;

                    IList<TacticalEvent> events = manager.Events;
                    data.eventCount = events != null ? events.Count : 0;
                    if (events != null)
                    {
                        int count = Math.Min(events.Count, MaxProbeTacticalEvents);
                        for (int i = 0; i < count; i++)
                            data.events.Add(TacticalEventData(events[i], i, manager.CurrentEvent, data.phase, sync));
                        data.omittedEventCount = Math.Max(0, events.Count - count);
                    }
                    if (manager.CurrentEvent != null)
                    {
                        int index = EventIndex(events, manager.CurrentEvent);
                        data.currentEvent = TacticalEventData(manager.CurrentEvent, index, manager.CurrentEvent, data.phase, sync);
                        data.currentEvent.timer = data.currentEventTimer;
                        data.currentEvent.realTimer = data.currentEventRealTimer;
                        data.currentEventId = data.currentEvent.hostId;
                    }
                    AddTacticalLocks(data, manager);
                }
                catch (Exception ex)
                {
                    data.error = ErrorText(ex);
                }
            }
            data.passButton = PassButtonData();
            data.delayButton = DelayButtonData();
            return data;
        }

        private static string TacticalPhase(TacticalEvent current, bool spinDownComplete, float spinUpTimer, bool guest)
        {
            if (current == null) return "None";
            if (!spinDownComplete) return "Ending";
            if (spinUpTimer > 0f) return "Starting";
            if (current.IsActive) return "Active";
            return guest ? "StartingOrEnding" : "Starting";
        }

        private static int EventIndex(IList<TacticalEvent> events, TacticalEvent sought)
        {
            if (events == null) return -1;
            for (int i = 0; i < events.Count; i++)
                if (ReferenceEquals(events[i], sought)) return i;
            return -1;
        }

        private static ProbeTacticalEvent TacticalEventData(TacticalEvent tacticalEvent, int index, TacticalEvent current, string phase, TacticalSyncContext sync)
        {
            if (tacticalEvent == null) return null;
            ProbeTacticalEvent data = new ProbeTacticalEvent
            {
                hostId = sync.HostEventId(tacticalEvent),
                index = index,
                kind = TacticalEventKindName(tacticalEvent),
                type = tacticalEvent.GetType().Name,
                current = ReferenceEquals(tacticalEvent, current),
                phase = ReferenceEquals(tacticalEvent, current) ? phase : "Queued",
                actor = TacticalActorData(tacticalEvent.Actor),
                attackTime = -1f,
                castAttackOccurrence = -1
            };
            try
            {
                data.active = tacticalEvent.IsActive;
                data.preview = tacticalEvent.IsPreview;
                data.visible = tacticalEvent.IsVisible;
                data.obscured = tacticalEvent.IsObscured;
                data.useSafeDestroy = tacticalEvent.UseSafeDestroy;
                data.initiativeDelay = tacticalEvent.InitiativeDelay;
                data.initiativeSort = tacticalEvent.InitiativeSort;
                data.initiativeBias = tacticalEvent.InitiativeBias;
                data.roundOffset = tacticalEvent.RoundOffset;
                data.timeOutDuration = tacticalEvent.TimeOutDuration;
                data.timeOutDurationRealTime = tacticalEvent.TimeOutDurationRealTime;
                data.spinUpDuration = tacticalEvent.SpinUpDuration;
                data.spinDownDuration = tacticalEvent.SpinDownDuration;
                TacticalTurnEvent turn = tacticalEvent as TacticalTurnEvent;
                if (turn != null)
                {
                    data.passStyle = turn.PassStyle.ToString();
                    data.skipTurnStart = turn.SkipTurnStart;
                    data.skipTurnEnd = turn.SkipTurnEnd;
                    data.previewInitiativeModifier = turn.PreviewInitiativeModifier;
                }
                TacticalCastFinishedEvent cast = tacticalEvent as TacticalCastFinishedEvent;
                if (cast != null)
                {
                    data.surpriseRound = cast.IsSurpriseRound;
                    data.attackTime = cast.AttackTime;
                    data.castAttackKey = CastAttackKey(cast.Attack, tacticalEvent.Actor, tacticalEvent.ActorStats, out data.castAttackName, out data.castAttackOccurrence);
                }
            }
            catch (Exception ex)
            {
                data.error = ErrorText(ex);
            }
            return data;
        }

        private static string TacticalEventKindName(TacticalEvent tacticalEvent)
        {
            if (tacticalEvent is TacticalCastFinishedEvent) return "CastFinished";
            if (tacticalEvent is TacticalTurnEvent) return "Turn";
            if (tacticalEvent is TacticalRoundStartEvent) return "RoundStart";
            if (tacticalEvent is TacticalDelayEvent) return "Delay";
            if (tacticalEvent is TacticalTurnLockedEvent) return "TurnLocked";
            if (tacticalEvent is TacticalProjectileTurnEvent) return "ProjectileTurn";
            return tacticalEvent.GetType().Name;
        }

        private static ProbeTacticalActor TacticalActorData(AIController actor)
        {
            if (actor == null) return null;
            string name;
            try { name = CharacterStats.Name(actor) ?? ""; }
            catch (Exception) { name = actor.name ?? ""; }
            return new ProbeTacticalActor
            {
                name = name,
                id = NetworkEntityId(actor.gameObject),
                instanceId = SavedInstanceId(actor.gameObject),
                type = actor.GetType().Name
            };
        }

        private static string CastAttackKey(AttackBase attack, AIController actor, CharacterStats stats, out string attackName, out int attackOccurrence)
        {
            attackName = "";
            attackOccurrence = -1;
            if (attack == null) return "";
            attackName = TacticalName(attack.name);
            GenericAbility ability = attack.AbilityOrigin;
            List<AttackBase> candidates = new List<AttackBase>();
            if (ability != null)
            {
                if (ability.Attack != null) candidates.Add(ability.Attack);
                foreach (AttackBase candidate in ability.GetComponents<AttackBase>())
                    if (candidate != null && !candidates.Contains(candidate)) candidates.Add(candidate);
            }
            else if (actor != null)
            {
                candidates.AddRange(actor.GetComponentsInChildren<AttackBase>(true));
            }
            int occurrence = 0;
            foreach (AttackBase candidate in candidates)
            {
                if (candidate == null || TacticalName(candidate.name) != attackName) continue;
                if (candidate == attack)
                {
                    attackOccurrence = occurrence;
                    break;
                }
                occurrence++;
            }
            string abilityKey = ability != null ? AbilityKeyWithOccurrence(ability, stats) : "";
            return attackName + "\u001f" + attackOccurrence + "\u001f" + abilityKey;
        }

        private static void AddTacticalLocks(ProbeTacticalState data, TacticalModeManager manager)
        {
            IEnumerable locks = PrivateObject(manager, "m_turnLockedBy") as IEnumerable;
            if (locks == null) return;
            ICollection collection = locks as ICollection;
            data.lockCount = collection != null ? collection.Count : 0;
            data.omittedLockCount = Math.Max(0, data.lockCount - MaxProbeTacticalLocks);
            foreach (object entry in locks)
            {
                if (entry == null || data.locks.Count >= MaxProbeTacticalLocks) break;
                object owner = MemberObject(entry, "Object");
                float timeOut = MemberValue(entry, "TimeOutTime", 0f);
                GameObject ownerObject = owner as GameObject;
                Component component = owner as Component;
                if (ownerObject == null && component != null) ownerObject = component.gameObject;
                string text = "";
                try { text = owner != null ? owner.ToString() : ""; }
                catch (Exception) { }
                data.locks.Add(new ProbeTacticalLock
                {
                    name = BoundedText(text, 256),
                    type = owner != null ? owner.GetType().Name : "",
                    ownerId = NetworkEntityId(ownerObject),
                    timeOutTime = timeOut,
                    timeLeft = timeOut - Time.time
                });
            }
        }

        private static ProbeTacticalButton PassButtonData()
        {
            UIPassTurnButton[] buttons = UnityEngine.Object.FindObjectsOfType<UIPassTurnButton>();
            if (buttons.Length == 0) return null;
            UIPassTurnButton source = buttons[0];
            UIMultiSpriteImageButton button = PrivateObject(source, "m_button") as UIMultiSpriteImageButton;
            AIController actor = PrivateObject(source, "m_currentActor") as AIController;
            AIController queued = PrivateObject(source, "m_queuedEndTurnActor") as AIController;
            return TacticalButtonData("pass", source, button, actor, queued);
        }

        private static ProbeTacticalButton DelayButtonData()
        {
            UIDelayTurnButton[] buttons = UnityEngine.Object.FindObjectsOfType<UIDelayTurnButton>();
            if (buttons.Length == 0) return null;
            UIDelayTurnButton source = buttons[0];
            UIMultiSpriteImageButton button = PrivateObject(source, "m_button") as UIMultiSpriteImageButton;
            AIController actor = PrivateObject(source, "m_actor") as AIController;
            return TacticalButtonData("delay", source, button, actor, null);
        }

        private static ProbeTacticalButton TacticalButtonData(string kind, MonoBehaviour source, UIMultiSpriteImageButton button, AIController actor, AIController queued)
        {
            ProbeTacticalButton data = new ProbeTacticalButton
            {
                kind = kind,
                name = source.name,
                actorId = actor != null ? NetworkEntityId(actor.gameObject) : "",
                queuedActorId = queued != null ? NetworkEntityId(queued.gameObject) : ""
            };
            try
            {
                Component control = button != null ? (Component)button : source;
                data.enabled = button != null && button.enabled;
                data.active = control.gameObject.activeInHierarchy;
                data.clientScreen = ControlScreen(control);
            }
            catch (Exception ex)
            {
                data.error = ErrorText(ex);
            }
            return data;
        }

        private static ProbeScreen ControlScreen(Component control)
        {
            if (control == null) return null;
            UICamera ui = UICamera.FindCameraForLayer(control.gameObject.layer);
            Camera camera = ui != null ? ui.cachedCamera : NGUITools.FindCameraForLayer(control.gameObject.layer);
            if (camera == null) return null;
            Collider collider = control.GetComponent<Collider>();
            if (collider == null) collider = control.GetComponentInChildren<Collider>();
            if (collider != null) return ScreenPoint(camera, collider.bounds.center);
            UIWidget widget = control.GetComponent<UIWidget>();
            if (widget == null) widget = control.GetComponentInChildren<UIWidget>();
            return widget != null ? WidgetScreen(widget) : ScreenPoint(camera, control.transform.position);
        }

        private static void CharacterData(ProbeCombatCharacter data, GameObject obj)
        {
            data.instanceId = SavedInstanceId(obj);
            Health health = obj.GetComponent<Health>();
            if (health != null)
            {
                data.currentHealth = health.CurrentHealth;
                data.maxHealth = health.MaxHealth;
                data.currentStamina = health.CurrentStamina;
                data.maxStamina = health.MaxStamina;
                data.dead = health.Dead;
                data.unconscious = health.Unconscious;
            }
            CharacterStats stats = obj.GetComponent<CharacterStats>();
            if (stats != null)
            {
                foreach (StatusEffect effect in stats.ActiveStatusEffects)
                {
                    if (effect == null) continue;
                    try
                    {
                        data.effects.Add(EffectData(effect));
                    }
                    catch (Exception ex)
                    {
                        data.effects.Add(new ProbeStatusEffect { effectID = effect.EffectID, error = ex.GetType().Name + ": " + ex.Message });
                    }
                }
                Dictionary<string, int> occurrences = new Dictionary<string, int>();
                foreach (GenericAbility ability in stats.ActiveAbilities)
                {
                    if (ability == null) continue;
                    data.abilities.Add(AbilityData(ability, stats, NextAbilityOccurrence(ability, occurrences)));
                }
            }
            AnimationController animation = obj.GetComponent<AnimationController>();
            if (animation != null)
            {
                AnimationController.Action desired = animation.DesiredAction;
                if (desired != null)
                {
                    data.desiredAction = desired.m_actionType.ToString();
                    data.desiredActionVariation = desired.m_variation;
                    data.desiredActionSpeed = desired.m_speed;
                    data.desiredActionOffhand = desired.m_offhand;
                }
                AnimationController.Action current = animation.CurrentAction;
                if (current != null)
                {
                    data.currentAction = current.m_actionType.ToString();
                    data.currentActionVariation = current.m_variation;
                    data.currentActionSpeed = current.m_speed;
                    data.currentActionOffhand = current.m_offhand;
                }
                data.currentReaction = animation.CurrentReaction.ToString();
            }
            TacticalCharacterData(data, obj);
        }

        private static void TacticalCharacterData(ProbeCombatCharacter data, GameObject obj)
        {
            try
            {
                CharacterStats stats = obj.GetComponent<CharacterStats>();
                if (stats != null)
                {
                    data.currentInitiative = stats.CurrentInitiative;
                    data.tempInitiative = stats.TempInitiative;
                    data.previousBaseInitiative = stats.PreviousBaseInitiative;
                    data.previousBaseReload = stats.PreviousBaseReload;
                    data.previousInitiativeIncrement = stats.PreviousInitiativeIncrement;
                    data.initiativeDelay = stats.InitiativeDelay;
                    data.initiativeDelayFromReload = stats.InitiativeDelayFromReload;
                }
                AIController actor = GameUtilities.FindActiveAIController(obj);
                if (actor == null) return;
                data.baseInitiative = actor.GetBaseInitiative(true);
                data.baseInitiativeNoReload = actor.GetBaseInitiative(false);
                data.availableAction = actor.AvailableAction;
                data.availableMovement = actor.AvailableMovement;
                data.availableMoveDistance = actor.AvailableMoveDistance;
                data.cachedAvailableMoveDistance = actor.CachedAvailableMoveDistance;
                Mover mover = actor.Mover;
                if (mover != null)
                {
                    data.moverEnabled = mover.isActiveAndEnabled;
                    data.moverFrozen = mover.Frozen;
                    data.moverDesiredSpeed = mover.DesiredSpeed;
                    data.moverCurrentSpeed = mover.Speed;
                    data.moverRunSpeed = mover.GetRunSpeed();
                    data.moverWalkSpeed = mover.GetWalkSpeed();
                }
                data.turnEventCount = actor.TacticalTurnEventCount;
                data.usedFreeAction = actor.UsedFreeAction();
                data.usedFreePotion = actor.WasFreePotionUsed();
                data.switchedWeaponsThisTurn = actor.SwitchedWeaponsThisTurn;
                data.evaluateTarget = actor.EvaluateTarget;
                TacticalModeManager manager = TacticalModeManager.Instance;
                data.isTakingTurn = manager != null && manager.IsTakingTurn(actor);
                data.inTacticalCombat = TacticalModeManager.IsInTacticalCombat(actor);
                try { data.canPass = data.isTakingTurn && CanPassTurn(manager, actor); }
                catch (Exception ex) { data.tacticalError = ErrorText(ex); }
                try { data.canDelay = data.isTakingTurn && manager.GetCanDelayTurn(actor); }
                catch (Exception ex) { data.tacticalError = ErrorText(ex); }
                IEnumerable freeActions = PrivateObject(actor, "UsedFreeActions") as IEnumerable;
                if (freeActions == null) return;
                foreach (object item in freeActions)
                {
                    GenericAbility ability = item as GenericAbility;
                    if (ability == null) continue;
                    data.freeActionCount++;
                    if (data.freeActions.Count < MaxProbeFreeActions)
                        data.freeActions.Add(AbilityKeyWithOccurrence(ability, stats));
                }
            }
            catch (Exception ex)
            {
                data.tacticalError = ErrorText(ex);
            }
        }

        private static bool CanPassTurn(TacticalModeManager manager, AIController actor)
        {
            if (manager == null || actor == null || !actor.IsControllablePartyMember()) return false;
            if (manager.HasLongCastEvent(actor)) return true;
            AIState current = actor.StateManager != null ? actor.StateManager.CurrentState : null;
            return current == null || current is Move || current is AI.Player.Wait || current is AI.Achievement.PathToPosition;
        }

        private static ProbeStatusEffect EffectData(StatusEffect effect)
        {
            MonoBehaviour origin = effect.Origin;
            GenericAbility originAbility = origin as GenericAbility;
            StatusEffectParams parameters = effect.Params;
            return new ProbeStatusEffect
            {
                effectID = effect.EffectID,
                bundleId = effect.BundleId,
                name = effect.GetDisplayName() ?? "",
                bundleName = effect.BundleName ?? "",
                origin = origin != null ? origin.name.Replace("(Clone)", "") : "",
                originKind = origin != null ? origin.GetType().Name : "",
                originId = originAbility != null ? AbilityKey(originAbility) : origin != null ? NetworkEntityId(origin.gameObject) : "",
                originDisplayID = originAbility != null ? AbilityDisplayId(originAbility) : -1,
                source = effect.Source != null ? effect.Source.name : "",
                sourceId = NetworkEntityId(effect.Source),
                tag = parameters != null ? parameters.Tag ?? "" : "",
                keyword = parameters != null ? parameters.Keyword ?? "" : "",
                stat = parameters != null ? parameters.AffectsStat.ToString() : "",
                duration = effect.Duration,
                timeLeft = effect.TimeLeft,
                timeActive = effect.TimeActive,
                timeApplied = effect.TimeApplied,
                applied = effect.Applied,
                suppressed = effect.IsSuppressed,
                suspended = effect.IsSuspended,
                tacticalClear = PrivateValue(effect, "m_TacticalClear", false)
            };
        }

        private static ProbeAbility AbilityData(GenericAbility ability, CharacterStats stats, int occurrence)
        {
            ProbeAbility data = new ProbeAbility
            {
                normalizedName = NormalizedAbilityName(ability),
                displayID = AbilityDisplayId(ability),
                occurrence = occurrence,
                attackTime = -1f,
                previewRecoveryTime = -1f
            };
            try
            {
                data.name = ability.Name() ?? "";
                data.cooldown = ability.Cooldown;
                data.maxCooldown = ability.MaxCooldown;
                data.cooldownType = ability.CooldownType.ToString();
                data.usesLeft = ability.UsesLeft();
                data.unlimitedUses = data.usesLeft == int.MaxValue;
                data.inCooldownRecovery = ability.IsInCooldownRecovery;
                data.inCooldownAtMax = ability.IsInCooldownAtMax;
                data.modal = ability.Modal;
                data.modalGroup = ability.Grouping.ToString();
                data.activated = ability.Activated;
                data.uiActivated = ability.UiActivated;
                data.applied = ability.Applied;
                data.inModalRecovery = ability.Modal && stats != null && stats.InModalRecovery(ability.Grouping);
                data.modalRecovery = ability.Modal && stats != null ? stats.GetModalRecovery(ability.Grouping) : 0f;
                data.ready = ability.Ready;
                data.readyForUI = ability.ReadyForUI;
                data.whyNotReady = ability.WhyNotReady.ToString();
                data.actionType = ability.ActionType.ToString();
                AttackBase attack = ability.Attack;
                data.hasAttack = attack != null;
                data.isLongCast = attack != null ? attack.IsLongCast : ability.ActionType == AttackBase.TacticalActionType.Cast;
                data.isFreeAction = attack != null ? attack.IsFreeAction : ability.ActionType == AttackBase.TacticalActionType.Free;
                if (attack != null)
                {
                    data.attackTime = attack.GetActionTime();
                    data.previewRecoveryTime = attack.GetPreviewRecoveryTime();
                }
                AIController owner = ability.Owner != null ? GameUtilities.FindActiveAIController(ability.Owner) : null;
                data.wasFreeActionUsed = owner != null && owner.WasFreeActionUsed(ability);
            }
            catch (Exception ex)
            {
                data.error = ex.GetType().Name + ": " + ex.Message;
            }
            return data;
        }

        private static string NormalizedAbilityName(GenericAbility ability)
        {
            return ability != null && ability.name != null ? ability.name.Replace("(Clone)", "") : "";
        }

        private static int AbilityDisplayId(GenericAbility ability)
        {
            return ability != null && ability.DisplayName != null ? ability.DisplayName.StringID : -1;
        }

        private static string AbilityKey(GenericAbility ability)
        {
            return NormalizedAbilityName(ability) + "\u001f" + AbilityDisplayId(ability);
        }

        private static string AbilityKeyWithOccurrence(GenericAbility ability, CharacterStats stats)
        {
            return TacticalName(ability != null ? ability.name : null) + "\u001f" + AbilityDisplayId(ability) + "\u001f" + AbilityOccurrence(ability, stats);
        }

        private static int NextAbilityOccurrence(GenericAbility ability, Dictionary<string, int> occurrences)
        {
            string key = AbilityKey(ability);
            occurrences.TryGetValue(key, out int occurrence);
            occurrences[key] = occurrence + 1;
            return occurrence;
        }

        private static int AbilityOccurrence(GenericAbility ability, CharacterStats stats)
        {
            if (stats == null) return 0;
            Dictionary<string, int> occurrences = new Dictionary<string, int>();
            foreach (GenericAbility candidate in stats.ActiveAbilities)
            {
                if (candidate == null) continue;
                int occurrence = NextAbilityOccurrence(candidate, occurrences);
                if (candidate == ability) return occurrence;
            }
            return 0;
        }

        private static GenericAbility ButtonAbility(UIAbilityBarButton button)
        {
            try
            {
                return button.TargetAbilityGenerous;
            }
            catch (Exception)
            {
                return button.TargetAbility;
            }
        }

        private static ProbeScreen WidgetScreen(UIWidget widget)
        {
            if (widget == null) return null;
            UICamera ui = UICamera.FindCameraForLayer(widget.gameObject.layer);
            Camera camera = ui != null ? ui.cachedCamera : NGUITools.FindCameraForLayer(widget.gameObject.layer);
            if (camera == null) return null;
            Vector3[] corners = NGUIMath.CalculateWidgetCorners(widget);
            return ScreenPoint(camera, (corners[0] + corners[2]) * 0.5f);
        }

        private static string SavedInstanceId(GameObject obj)
        {
            InstanceID id = obj != null ? obj.GetComponent<InstanceID>() : null;
            return id != null ? id.Guid.ToString() : "";
        }

        private static string NetworkEntityId(GameObject obj)
        {
            if (obj == null) return "";
            Guid id = EntityRegistry.NetworkId(obj);
            return id != Guid.Empty ? id.ToString() : SavedInstanceId(obj);
        }

        private static string NetworkItemId(Item item)
        {
            Guid id = ItemRegistry.NetworkId(item);
            return id != Guid.Empty ? id.ToString() : "";
        }

        private static string NetworkInventoryId(BaseInventory inventory)
        {
            Guid id = InventorySync.IdOf(inventory);
            return id != Guid.Empty ? id.ToString() : inventory != null ? SavedInstanceId(inventory.gameObject) : "";
        }

        private sealed class TacticalSyncContext
        {
            internal Type SyncType;
            internal object Instance;
            internal long Epoch;
            internal long Sequence = -1;
            internal long CurrentEventId;
            internal long PendingEpoch;
            internal long PendingSequence = -1;
            internal bool Full, FullKnown;
            internal bool ApplyingHostState;
            internal bool MetadataAvailable, IsHost, IsGuest;
            internal object Pending;
            internal string PendingReason = "", PendingPhase = "", FullSource = "unavailable";

            internal static TacticalSyncContext Create()
            {
                TacticalSyncContext context = new TacticalSyncContext
                {
                    SyncType = typeof(DevTools).Assembly.GetType("PoECoop.Sync.TacticalStateSync", false)
                };
                if (context.SyncType == null) return context;
                context.MetadataAvailable = true;
                context.Instance = FindSyncInstance(context.SyncType);
                Session session = SyncValue(context, new[] { "Session" }) as Session;
                context.IsHost = session != null && session.IsHost;
                context.IsGuest = session != null && session.IsGuest;
                context.Pending = SyncValue(context, new[] { "Pending" });
                context.Epoch = SyncInt64(context, new[] { "CurrentEpoch", "Epoch", "TacticalEpoch" }, 0);
                context.Sequence = context.IsGuest
                    ? SyncInt64(context, new[] { "LastSequence", "LastSnapshotSequence", "Sequence", "CurrentSequence" }, -1)
                    : SyncInt64(context, new[] { "Sequence", "CurrentSequence", "SnapshotSequence", "LastSequence" }, -1);
                context.CurrentEventId = SyncInt64(context, new[] { "CurrentEventId", "CurrentHostEventId", "EventId" }, 0);
                context.ApplyingHostState = SyncBoolean(context, new[] { "ApplyingHostState" }, false);
                if (context.Pending != null)
                {
                    context.Full = PrivateValue(context.Pending, "Full", false);
                    context.FullKnown = true;
                    context.FullSource = "pendingSnapshot";
                    object pendingPhase = PrivateObject(context.Pending, "Phase");
                    context.PendingPhase = pendingPhase != null ? pendingPhase.ToString() : "";
                    context.PendingEpoch = PrivateValue(context.Pending, "Epoch", 0);
                    context.PendingSequence = PrivateValue(context.Pending, "Sequence", -1);
                }
                else if (context.IsHost)
                {
                    object lastSignature = SyncValue(context, new[] { "LastSignature" });
                    float nextFull = SyncSingle(context, new[] { "NextFullRefresh" }, 0f);
                    context.Full = lastSignature == null || Time.unscaledTime >= nextFull;
                    context.FullKnown = true;
                    context.FullSource = "hostNextCapture";
                }
                context.PendingReason = DescribePendingSnapshot(context);
                return context;
            }

            internal long HostEventId(TacticalEvent tacticalEvent)
            {
                if (tacticalEvent == null || SyncType == null) return 0;
                TacticalModeManager manager = TacticalModeManager.Instance;
                if (manager != null && ReferenceEquals(manager.CurrentEvent, tacticalEvent)) return CurrentEventId;
                long id = EventIdFromFields(SyncType, null, tacticalEvent);
                if (id == 0 && Instance != null) id = EventIdFromFields(SyncType, Instance, tacticalEvent);
                if (id == 0 && Instance != null)
                {
                    object presentation = NamedValue(SyncType, Instance, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, new[] { "Presentation" });
                    if (presentation != null) id = EventIdFromFields(presentation.GetType(), presentation, tacticalEvent);
                }
                if (id == 0 && IsHost) id = InvokeEventIdMethod(tacticalEvent);
                return id;
            }

            private long InvokeEventIdMethod(TacticalEvent tacticalEvent)
            {
                const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (MethodInfo method in SyncType.GetMethods(flags))
                {
                    string name = NormalizedMemberName(method.Name);
                    if (!NameIs(name, "HostEventId", "GetHostEventId", "EventId", "GetEventId", "IdOf", "HostId")) continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 1 || !parameters[0].ParameterType.IsAssignableFrom(tacticalEvent.GetType())) continue;
                    object target = method.IsStatic ? null : Instance;
                    if (!method.IsStatic && target == null) continue;
                    try
                    {
                        if (TryInt64(method.Invoke(target, new object[] { tacticalEvent }), out long id)) return id;
                    }
                    catch (Exception) { }
                }
                return 0;
            }

            private static long EventIdFromFields(Type type, object target, TacticalEvent tacticalEvent)
            {
                BindingFlags flags = (target == null ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.Public | BindingFlags.NonPublic;
                for (Type current = type; current != null; current = current.BaseType)
                {
                    foreach (FieldInfo field in current.GetFields(flags | BindingFlags.DeclaredOnly))
                    {
                        if (!typeof(IEnumerable).IsAssignableFrom(field.FieldType) || field.FieldType == typeof(string)) continue;
                        object value;
                        try { value = field.GetValue(target); }
                        catch (Exception) { continue; }
                        IDictionary dictionary = value as IDictionary;
                        if (dictionary == null) continue;
                        try
                        {
                            if (dictionary.Contains(tacticalEvent) && TryInt64(dictionary[tacticalEvent], out long direct)) return direct;
                        }
                        catch (Exception) { }
                        try
                        {
                            int inspected = 0;
                            foreach (DictionaryEntry entry in dictionary)
                            {
                                if (++inspected > 1024) break;
                                if (ReferenceEquals(entry.Value, tacticalEvent) && TryInt64(entry.Key, out long reverse)) return reverse;
                            }
                        }
                        catch (Exception) { }
                    }
                }
                return 0;
            }
        }

        private static string DescribePendingSnapshot(TacticalSyncContext context)
        {
            if (context.Pending == null) return "";
            if (!Plugin.IsMirroring) return "waitingForMirroring";
            try
            {
                Dictionary<Guid, AIController> actors = new Dictionary<Guid, AIController>();
                IEnumerable actorRecords = PrivateObject(context.Pending, "Actors") as IEnumerable;
                int actorCount = 0;
                if (actorRecords != null)
                {
                    foreach (object record in actorRecords)
                    {
                        if (++actorCount > 128) return "actorCountExceedsSyncBound";
                        Guid id = PrivateValue(record, "Id", Guid.Empty);
                        GameObject obj = EntityRegistry.ResolveObject(id);
                        if (obj == null) obj = InstanceID.GetObjectByID(id);
                        AIController actor = obj != null ? GameUtilities.FindActiveAIController(obj) : null;
                        CharacterStats stats = actor != null ? actor.CharacterStats : null;
                        if (actor == null || stats == null) return "actor:" + id;
                        actors[id] = actor;
                        IEnumerable freeActions = PrivateObject(record, "FreeActions") as IEnumerable;
                        int freeCount = 0;
                        if (freeActions == null) continue;
                        foreach (object key in freeActions)
                        {
                            if (++freeCount > 32) return "freeActionCountExceedsSyncBound:" + id;
                            string name = PrivateValue(key, "Name", "");
                            int displayId = PrivateValue(key, "DisplayId", -1);
                            int occurrence = PrivateValue(key, "Occurrence", 0);
                            if (FindProbeAbility(stats, name, displayId, occurrence) == null)
                                return "freeAction:" + id + ":" + BoundedText(name, 96) + "#" + displayId + "/" + occurrence;
                        }
                    }
                }

                IEnumerable eventRecords = PrivateObject(context.Pending, "Events") as IEnumerable;
                int eventCount = 0;
                if (eventRecords != null)
                {
                    foreach (object record in eventRecords)
                    {
                        if (++eventCount > 512) return "eventCountExceedsSyncBound";
                        object kind = PrivateObject(record, "Kind");
                        if (kind == null || kind.ToString() != "CastFinished") continue;
                        Guid actorId = PrivateValue(record, "ActorId", Guid.Empty);
                        int eventId = PrivateValue(record, "Id", 0);
                        if (!actors.TryGetValue(actorId, out AIController actor)) return "castActor:" + eventId + ":" + actorId;
                        object attack = PrivateObject(record, "Attack");
                        if (attack == null) return "castAttackMissing:" + eventId;
                        if (FindProbeCastAttack(actor, actor.CharacterStats, attack) == null)
                        {
                            string name = PrivateValue(attack, "Name", "");
                            int occurrence = PrivateValue(attack, "Occurrence", 0);
                            object ability = PrivateObject(attack, "Ability");
                            string abilityKey = ability != null
                                ? PrivateValue(ability, "Name", "") + "#" + PrivateValue(ability, "DisplayId", -1) + "/" + PrivateValue(ability, "Occurrence", -1)
                                : "";
                            return "castAttack:" + eventId + ":" + BoundedText(name, 96) + "#" + occurrence + ":" + BoundedText(abilityKey, 140);
                        }
                    }
                }
                return "awaitingApplication";
            }
            catch (Exception ex)
            {
                return "probeError:" + ErrorText(ex);
            }
        }

        private static GenericAbility FindProbeAbility(CharacterStats stats, string name, int displayId, int occurrence)
        {
            if (stats == null || occurrence < 0) return null;
            int current = 0;
            foreach (GenericAbility ability in stats.ActiveAbilities)
            {
                if (ability == null || TacticalName(ability.name) != name || AbilityDisplayId(ability) != displayId) continue;
                if (current++ == occurrence) return ability;
            }
            return null;
        }

        private static AttackBase FindProbeCastAttack(AIController actor, CharacterStats stats, object identity)
        {
            string name = PrivateValue(identity, "Name", "");
            int soughtOccurrence = PrivateValue(identity, "Occurrence", 0);
            object abilityKey = PrivateObject(identity, "Ability");
            int abilityOccurrence = PrivateValue(abilityKey, "Occurrence", -1);
            List<AttackBase> candidates = new List<AttackBase>();
            if (abilityOccurrence >= 0)
            {
                string abilityName = PrivateValue(abilityKey, "Name", "");
                int displayId = PrivateValue(abilityKey, "DisplayId", -1);
                GenericAbility ability = FindProbeAbility(stats, abilityName, displayId, abilityOccurrence);
                if (ability == null) return null;
                if (ability.Attack != null) candidates.Add(ability.Attack);
                foreach (AttackBase candidate in ability.GetComponents<AttackBase>())
                    if (candidate != null && !candidates.Contains(candidate)) candidates.Add(candidate);
            }
            else if (actor != null)
            {
                candidates.AddRange(actor.GetComponentsInChildren<AttackBase>(true));
            }
            int occurrence = 0;
            foreach (AttackBase attack in candidates)
            {
                if (attack == null || TacticalName(attack.name) != name) continue;
                if (occurrence++ == soughtOccurrence) return attack;
            }
            return null;
        }

        private static string TacticalName(string name) => Protocol.Clean((name ?? "").Replace("(Clone)", ""), 96);

        private static object FindSyncInstance(Type syncType)
        {
            object instance = NamedValue(syncType, null, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, new[] { "Instance", "Current", "Singleton" });
            if (instance != null && syncType.IsInstanceOfType(instance)) return instance;
            Plugin[] plugins = UnityEngine.Object.FindObjectsOfType<Plugin>();
            foreach (Plugin plugin in plugins)
            {
                for (Type type = plugin.GetType(); type != null; type = type.BaseType)
                {
                    foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (!syncType.IsAssignableFrom(field.FieldType)) continue;
                        try
                        {
                            object value = field.GetValue(plugin);
                            if (value != null) return value;
                        }
                        catch (Exception) { }
                    }
                }
            }
            return null;
        }

        private static object MemberByNames(object target, params string[] names)
        {
            return target != null ? NamedValue(target.GetType(), target, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, names) : null;
        }

        private static long ObjectInt64(object value, long fallback)
        {
            return TryInt64(value, out long result) ? result : fallback;
        }

        private static int ObjectInt(object value, int fallback)
        {
            if (!TryInt64(value, out long result)) return fallback;
            return result > int.MaxValue ? int.MaxValue : result < int.MinValue ? int.MinValue : (int)result;
        }

        private static bool ObjectBool(object value, bool fallback)
        {
            if (value is bool) return (bool)value;
            if (value != null && bool.TryParse(value.ToString(), out bool result)) return result;
            return fallback;
        }

        private static int ObjectCount(object value)
        {
            ICollection collection = value as ICollection;
            if (collection != null) return collection.Count;
            if (value == null || value is string) return 0;
            IEnumerable enumerable = value as IEnumerable;
            if (enumerable == null) return 0;
            int count = 0;
            foreach (object ignored in enumerable)
            {
                count++;
                if (count >= 1024) break;
            }
            return count;
        }

        private static void CaptureFeatureValues(List<ProbeValue> output, ProbeFeatureContext feature, string prefix)
        {
            if (!feature.Available) return;
            if (feature.Instance == null)
            {
                output.Add(new ProbeValue { name = prefix + ".instanceAvailable", type = "bool", value = "false" });
                return;
            }
            CaptureValues(output, feature.Instance, prefix, 0, new List<object>());
        }

        private static void CaptureValues(List<ProbeValue> output, object target, string prefix, int depth, List<object> ancestors)
        {
            if (target == null || output.Count >= MaxProbeSyncValues || depth > 4) return;
            if (IsProbeScalar(target))
            {
                AddProbeValue(output, prefix, target);
                return;
            }
            byte[] bytes = target as byte[];
            if (bytes != null)
            {
                AddProbeText(output, prefix + ".length", bytes.Length.ToString());
                return;
            }
            UnityEngine.Object unityObject = target as UnityEngine.Object;
            if (unityObject != null)
            {
                output.Add(new ProbeValue { name = prefix, type = target.GetType().Name, value = BoundedText(unityObject.name, 256) });
                return;
            }
            if (target is Session || typeof(Delegate).IsAssignableFrom(target.GetType())) return;
            foreach (object ancestor in ancestors) if (ReferenceEquals(ancestor, target)) return;
            ancestors.Add(target);
            try
            {
                IDictionary dictionary = target as IDictionary;
                if (dictionary != null)
                {
                    int index = 0;
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (index++ >= 64 || output.Count >= MaxProbeSyncValues) break;
                        string path = prefix + "[" + BoundedText(entry.Key, 96) + "]";
                        CaptureValues(output, entry.Value, path, depth + 1, ancestors);
                    }
                    if (dictionary.Count > 64) AddProbeText(output, prefix + ".omitted", (dictionary.Count - 64).ToString());
                    return;
                }
                IEnumerable enumerable = target as IEnumerable;
                if (enumerable != null && !(target is string))
                {
                    int index = 0;
                    foreach (object value in enumerable)
                    {
                        if (index >= 64 || output.Count >= MaxProbeSyncValues) break;
                        CaptureValues(output, value, prefix + "[" + index + "]", depth + 1, ancestors);
                        index++;
                    }
                    ICollection collection = target as ICollection;
                    if (collection != null && collection.Count > index) AddProbeText(output, prefix + ".omitted", (collection.Count - index).ToString());
                    return;
                }
                if (target.GetType().Assembly != typeof(DevTools).Assembly)
                {
                    output.Add(new ProbeValue { name = prefix, type = target.GetType().FullName, value = BoundedText(target, 512) });
                    return;
                }
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                for (Type type = target.GetType(); type != null && output.Count < MaxProbeSyncValues; type = type.BaseType)
                {
                    foreach (FieldInfo field in type.GetFields(flags))
                    {
                        if (field.Name.IndexOf("k__BackingField", StringComparison.Ordinal) >= 0 || typeof(Delegate).IsAssignableFrom(field.FieldType)) continue;
                        object value;
                        try { value = field.GetValue(target); }
                        catch (Exception ex) { AddProbeText(output, prefix + "." + field.Name + ".error", ErrorText(ex)); continue; }
                        CaptureValues(output, value, prefix + "." + NormalizedMemberName(field.Name), depth + 1, ancestors);
                    }
                    foreach (PropertyInfo property in type.GetProperties(flags))
                    {
                        if (property.GetIndexParameters().Length != 0 || !property.CanRead || typeof(Delegate).IsAssignableFrom(property.PropertyType)) continue;
                        object value;
                        try { value = property.GetValue(target, null); }
                        catch (Exception ex) { AddProbeText(output, prefix + "." + property.Name + ".error", ErrorText(ex)); continue; }
                        CaptureValues(output, value, prefix + "." + property.Name, depth + 1, ancestors);
                    }
                }
            }
            finally
            {
                ancestors.RemoveAt(ancestors.Count - 1);
            }
        }

        private static bool IsProbeScalar(object value)
        {
            if (value == null) return true;
            Type type = value.GetType();
            return type.IsPrimitive || type.IsEnum || value is string || value is decimal || value is Guid || value is DateTime || value is TimeSpan || value is Vector2 || value is Vector3 || value is Vector4 || value is Quaternion;
        }

        private static void AddProbeValue(List<ProbeValue> output, string name, object value)
        {
            if (output.Count >= MaxProbeSyncValues) return;
            string text;
            if (value == null) text = "";
            else if (value is IFormattable) text = ((IFormattable)value).ToString(null, System.Globalization.CultureInfo.InvariantCulture);
            else text = value.ToString();
            output.Add(new ProbeValue { name = BoundedText(name, 256), type = value != null ? value.GetType().Name : "", value = BoundedText(text, 4096) });
        }

        private static void AddProbeText(List<ProbeValue> output, string name, string value)
        {
            if (output.Count < MaxProbeSyncValues) output.Add(new ProbeValue { name = BoundedText(name, 256), type = "string", value = BoundedText(value, 4096) });
        }

        private static long SyncInt64(TacticalSyncContext context, string[] names, long fallback)
        {
            object value = SyncValue(context, names);
            return TryInt64(value, out long result) ? result : fallback;
        }

        private static float SyncSingle(TacticalSyncContext context, string[] names, float fallback)
        {
            object value = SyncValue(context, names);
            if (value == null) return fallback;
            try { return Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception) { return fallback; }
        }

        private static bool SyncBoolean(TacticalSyncContext context, string[] names, bool fallback)
        {
            object value = SyncValue(context, names);
            if (value is bool) return (bool)value;
            if (value != null && bool.TryParse(value.ToString(), out bool result)) return result;
            return fallback;
        }

        private static object SyncValue(TacticalSyncContext context, string[] names)
        {
            foreach (string name in names)
            {
                object value = NamedValue(context.SyncType, null, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, new[] { name });
                if (value != null) return value;
                if (context.Instance != null)
                {
                    value = NamedValue(context.SyncType, context.Instance, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, new[] { name });
                    if (value != null) return value;
                }
            }
            return null;
        }

        private static object NamedValue(Type type, object target, BindingFlags flags, string[] names)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                foreach (FieldInfo field in current.GetFields(flags | BindingFlags.DeclaredOnly))
                {
                    if (!NameIs(NormalizedMemberName(field.Name), names)) continue;
                    try { return field.GetValue(target); }
                    catch (Exception) { }
                }
                foreach (PropertyInfo property in current.GetProperties(flags | BindingFlags.DeclaredOnly))
                {
                    if (!NameIs(NormalizedMemberName(property.Name), names) || property.GetIndexParameters().Length != 0) continue;
                    try { return property.GetValue(target, null); }
                    catch (Exception) { }
                }
            }
            return null;
        }

        private static List<ProbeTacticalCounter> BlockedGuestTacticalCounters()
        {
            List<ProbeTacticalCounter> counters = new List<ProbeTacticalCounter>();
            Type[] types;
            try { types = typeof(DevTools).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            foreach (Type type in types)
            {
                if (type == null || type.FullName == null || type.FullName.IndexOf("Tactical", StringComparison.OrdinalIgnoreCase) < 0) continue;
                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                foreach (FieldInfo field in type.GetFields(flags))
                {
                    if (field.IsLiteral || field.Name.IndexOf("k__BackingField", StringComparison.Ordinal) >= 0 || !IsBlockCounter(field.Name)) continue;
                    object value;
                    try { value = field.GetValue(null); }
                    catch (Exception) { continue; }
                    AddTacticalCounter(counters, type.FullName, field.Name, value);
                }
                foreach (PropertyInfo property in type.GetProperties(flags))
                {
                    if (!IsBlockCounter(property.Name) || property.GetIndexParameters().Length != 0) continue;
                    object value;
                    try { value = property.GetValue(null, null); }
                    catch (Exception) { continue; }
                    AddTacticalCounter(counters, type.FullName, property.Name, value);
                }
                if (counters.Count >= MaxProbeTacticalCounters) break;
            }
            counters.Sort((a, b) =>
            {
                int source = string.CompareOrdinal(a.source, b.source);
                return source != 0 ? source : string.CompareOrdinal(a.name, b.name);
            });
            return counters;
        }

        private static bool IsBlockCounter(string name)
        {
            return name.IndexOf("block", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("reject", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void AddTacticalCounter(List<ProbeTacticalCounter> counters, string source, string name, object value)
        {
            if (counters.Count >= MaxProbeTacticalCounters) return;
            if (TryInt64(value, out long count))
            {
                counters.Add(new ProbeTacticalCounter { source = source, name = name, count = count });
                return;
            }
            IDictionary dictionary = value as IDictionary;
            if (dictionary == null) return;
            int inspected = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (++inspected > MaxProbeTacticalCounters || counters.Count >= MaxProbeTacticalCounters) break;
                if (TryInt64(entry.Value, out long item))
                    counters.Add(new ProbeTacticalCounter { source = source, name = name + "." + BoundedText(entry.Key, 64), count = item });
            }
        }

        private static bool TryInt64(object value, out long result)
        {
            result = 0;
            if (value == null || value is bool || value is float || value is double || value is decimal) return false;
            try
            {
                result = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static bool NameIs(string actual, params string[] candidates)
        {
            foreach (string candidate in candidates)
                if (string.Equals(actual, candidate, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string NormalizedMemberName(string name)
        {
            if (name.StartsWith("<", StringComparison.Ordinal))
            {
                int end = name.IndexOf('>');
                if (end > 1) return name.Substring(1, end - 1);
            }
            if (name.StartsWith("s_", StringComparison.Ordinal) || name.StartsWith("m_", StringComparison.Ordinal) || name.StartsWith("_", StringComparison.Ordinal))
                return name.Substring(name.StartsWith("_", StringComparison.Ordinal) ? 1 : 2);
            return name;
        }

        private static object PrivateObject(object target, string name)
        {
            if (target == null) return null;
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(target);
                PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(target, null);
            }
            return null;
        }

        private static object MemberObject(object target, string name) => PrivateObject(target, name);

        private static T PrivateValue<T>(object target, string name, T fallback)
        {
            try
            {
                object value = PrivateObject(target, name);
                if (value is T) return (T)value;
                if (value != null) return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception) { }
            return fallback;
        }

        private static T MemberValue<T>(object target, string name, T fallback) => PrivateValue(target, name, fallback);

        private static string ErrorText(Exception ex) => BoundedText(ex.GetType().Name + ": " + ex.Message, 512);

        private static string BoundedText(object value, int maxLength)
        {
            string text = value != null ? value.ToString() : "";
            return text.Length <= maxLength ? text : text.Substring(0, maxLength);
        }

        private static ProbeVector VectorData(Vector3 point) => new ProbeVector { x = point.x, y = point.y, z = point.z };

        private static ProbeScreen ScreenPoint(Vector3 mouse) => new ProbeScreen
        {
            x = mouse.x,
            y = Screen.height - mouse.y,
            onScreen = mouse.x >= 0 && mouse.y >= 0 && mouse.x < Screen.width && mouse.y < Screen.height
        };

        private static ProbeScreen ScreenPoint(Camera camera, Vector3 world)
        {
            Vector3 point = camera.WorldToScreenPoint(world);
            ProbeScreen screen = ScreenPoint(point);
            screen.onScreen &= point.z > 0;
            return screen;
        }

        private static ProbeEntity EntityData(GameObject obj, Camera camera, string kind = null)
        {
            if (obj == null) return null;
            var id = obj.GetComponent<InstanceID>();
            return new ProbeEntity
            {
                name = obj.name,
                kind = kind ?? obj.GetType().Name,
                id = id != null ? id.Guid.ToString() : "",
                world = VectorData(obj.transform.position),
                screen = ScreenPoint(camera, obj.transform.position)
            };
        }

        private static IEnumerator LogPositions(Session session)
        {
            int lastFrame = Time.frameCount;
            float lastTime = Time.unscaledTime;
            int tick = 0;
            while (true)
            {
                yield return new WaitForSecondsRealtime(1f);
                if (!InLevel) continue;
                var line = new System.Text.StringBuilder("POS t=" + DateTime.Now.ToString("HH:mm:ss.f"));
                foreach (PartyMemberAI member in PartyMemberAI.PartyMembers)
                    if (member != null)
                    {
                        line.Append("  ").Append(member.Slot).Append('=').Append(member.transform.position.ToString("0.00"))
                            .Append(member.Selected ? " sel" : " -");
                    }
                // Try to take everyone the ways a player can: select-all, then a click on each.
                // Both are refused for characters that are not the local player's.
                if (++tick % 2 == 0) PartyMemberAI.SelectAll();
                else foreach (PartyMemberAI member in PartyMemberAI.PartyMembers) if (member != null) member.ExclusiveSelect();
                float fps = (Time.frameCount - lastFrame) / Mathf.Max(0.001f, Time.unscaledTime - lastTime);
                lastFrame = Time.frameCount; lastTime = Time.unscaledTime;
                line.Append("  fps=").Append(fps.ToString("0"));
                session.Note(line.ToString());
            }
        }
    }
}
