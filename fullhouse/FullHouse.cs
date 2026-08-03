// FullHouse - self-contained "raise the co-op lobby cap" engine for Schedule I (MelonLoader).
//
// One file, two uses:
//   * Standalone: build with FULLHOUSE_STANDALONE defined -> a drop-in MelonMod (FullHouse.dll) that
//     raises the cap on its own.
//   * Embedded: linked as source into another mod (e.g. SideHustle via Workspace/build/FullHouse.props);
//     the host calls FullHouse.Install() and gets native bigger lobbies with no external dependency.
//
// Since 0.4.6f11 the game hides Steam behind ScheduleOne.Networking.ILobbyService: Lobby owns no callbacks,
// no seat array and no Steam id any more, it delegates to SteamLobbyService (or MockLobbyService when Steam
// is down). So every patch below targets SteamLobbyService and LobbyInterface:
//   - grow the fixed SteamLobbyService._players[4] seat store (UpdateLobbyMembers writes into it with no
//     bounds check, so member #5 would throw IndexOutOfRange inside a Steam callback),
//   - raise the requested member count in CreateLobby and again post-creation via SetLobbyMemberLimit,
//   - replace the invite gate, whose "already at max capacity" check is a hard-coded 4,
//   - clone the lobby UI slots and keep the "/N" title and the invite button in sync,
//   - raise the NETWORK TRANSPORT's own client limit, which is a separate cap from the lobby's: seats
//     the transport will not accept are seats players can occupy in the lobby but never connect into.
// All patches are idempotent and additive (only ever grow, never skip the original), so it coexists with
// other cap mods (e.g. BiggerLobbies): the highest cap wins and nothing conflicts. A named-GameObject
// single-flight guard means only one loaded copy installs the patches.
//
// NOTE for future edits: transpilers are useless here. Under MelonLoader/IL2CPP a Harmony patch detours the
// native method pointer; there is no managed IL body to rewrite, so a literal-replacing transpiler silently
// does nothing. Every cap literal must be handled by a prefix or postfix.
//
// The class is INTERNAL so it can be compiled into several assemblies without a CS0436 clash.

#if IL2CPP
using Il2CppScheduleOne.Networking;
using Il2CppScheduleOne.UI.Multiplayer;
using Il2CppScheduleOne.DevUtilities;
using Il2CppSteamworks;
using Il2Cpp;                       // SteamManager (global-namespace Steamworks.NET helper)
using Il2CppInterop.Runtime.InteropTypes.Arrays;
// The transport type's name equals its namespace, so alias it rather than fight the ambiguity.
using FishyTransport = Il2CppFishySteamworks.FishySteamworks;
#else
using ScheduleOne.Networking;
using ScheduleOne.UI.Multiplayer;
using ScheduleOne.DevUtilities;
using Steamworks;
using FishyTransport = FishySteamworks.FishySteamworks;
#endif
using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

#if FULLHOUSE_STANDALONE
[assembly: MelonInfo(typeof(DooDesch.FullHouse.Core), "FullHouse", "1.1.0", "DooDesch", "https://github.com/DooDesch-Mods/ScheduleOne-FullHouse")]
[assembly: MelonGame("TVGS", "Schedule I")]
#endif

namespace DooDesch.FullHouse
{
#if FULLHOUSE_STANDALONE
    /// <summary>Standalone MelonMod entry. Compiled only into the standalone FullHouse.dll; excluded when the
    /// engine is embedded as linked source into a host mod (which calls <see cref="Lobbies.Install"/> itself).</summary>
    public sealed class Core : MelonMod
    {
        public override void OnInitializeMelon() => Lobbies.Install();
    }
#endif

    /// <summary>The cap-raising engine. Call <see cref="Install"/> once (early - e.g. OnInitializeMelon).</summary>
    internal static class Lobbies
    {
        internal const int DefaultCapacity = 32;
        private const int HardMax = 250;   // Steam's absolute lobby member ceiling
        private const int SafeMax = 32;    // above this we log a warning
        private const string GuardName = "___DooDesch_FullHouse___";

        private static bool _installed;
        private static MelonPreferences_Entry<int> _capEntry;

        /// <summary>The seat capacity, clamped to a sane range. Read from MelonPreferences once registered.</summary>
        internal static int Capacity
        {
            get
            {
                int c = _capEntry != null ? _capEntry.Value : DefaultCapacity;
                if (c < 2) c = 2;
                if (c > HardMax) c = HardMax;
                return c;
            }
        }

        /// <summary>The host's advertised cap, learned from the lobby's "max_players" data when we join. Lets a
        /// client configured smaller than the host still seat everyone the host admits. Only ever grows WITHIN a
        /// lobby, and is dropped when we leave - otherwise one visit to a 32-seat lobby would keep raising the cap of
        /// every later lobby we host ourselves.</summary>
        private static int _hostCap;

        /// <summary>The cap actually applied on this client - the larger of our own setting and the host's, clamped
        /// to the hard ceiling. This is what the seat array, invite gate and UI use, so a client adapts up to its host.</summary>
        internal static int EffectiveCap => Math.Min(HardMax, Math.Max(Capacity, _hostCap));

        /// <summary>The Steam id of the lobby we are currently in, or 0. Lobby.LobbyID exists in 0.4.6f11 but the
        /// game never assigns it, so it is always 0 - the real id lives in SteamLobbyService._lobbyID.</summary>
        internal static ulong CurrentLobbyId
        {
            get
            {
                try { var s = Service(); return s != null ? s._lobbyID : 0UL; }
                catch { return 0UL; }
            }
        }

        /// <summary>How many seats the game's lobby service actually holds right now, or 0 when there is no Steam
        /// lobby service. This is the ground truth a host should size a session by - it reflects whatever every
        /// loaded cap mod has grown the array to, not just our own setting.</summary>
        internal static int SeatCount
        {
            get
            {
                try { var s = Service(); var p = s?._players; return p != null ? p.Length : 0; }
                catch { return 0; }
            }
        }

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;

            // Cross-assembly single-flight: the standalone DLL and a SideHustle-embedded copy can both be loaded.
            // The Unity scene graph is process-global, so a named DontDestroyOnLoad object is a marker both
            // separately-compiled copies can see. First one wins; the rest stand down (patching is idempotent
            // anyway, but this also avoids double UI cloning).
            try
            {
                if (GameObject.Find(GuardName) != null)
                {
                    MelonLogger.Msg("[FullHouse] another copy is already active - standing down.");
                    return;
                }
                var guard = new GameObject(GuardName);
                UnityEngine.Object.DontDestroyOnLoad(guard);
                guard.hideFlags = HideFlags.HideAndDontSave;
            }
            catch { /* if the marker can't be made this early, fall through and still install */ }

            try
            {
                var cat = MelonPreferences.CreateCategory("FullHouse", "FullHouse");
                _capEntry = cat.CreateEntry("Capacity", DefaultCapacity, "Max lobby players",
                    "Maximum co-op players FullHouse seats (2-250). Values above 32 are unsupported and may destabilise the game.");
            }
            catch (Exception e) { MelonLogger.Warning("[FullHouse] preference registration failed: " + e.Message); }

            if (Capacity > SafeMax)
                MelonLogger.Warning($"[FullHouse] capacity {Capacity} exceeds the tested maximum of {SafeMax} - expect instability.");

            try
            {
                var h = new HarmonyLib.Harmony("com.doodesch.fullhouse");
                Patch(h, typeof(SteamLobbyService), "Initialize", postfix: nameof(Service_Initialize_Postfix));
                Patch(h, typeof(SteamLobbyService), "UpdateLobbyMembers", prefix: nameof(Service_UpdateLobbyMembers_Prefix));
                Patch(h, typeof(SteamLobbyService), "CreateLobby", prefix: nameof(Service_CreateLobby_Prefix));
                Patch(h, typeof(SteamLobbyService), "OnLobbyCreated", postfix: nameof(Service_OnLobbyCreated_Postfix));
                Patch(h, typeof(SteamLobbyService), "OnLobbyEntered", postfix: nameof(Service_OnLobbyEntered_Postfix));
                Patch(h, typeof(SteamLobbyService), "OpenInviteUI", prefix: nameof(Service_OpenInviteUI_Prefix));
                Patch(h, typeof(SteamLobbyService), "LeaveLobby", postfix: nameof(Service_LeaveLobby_Postfix));
                Patch(h, typeof(LobbyInterface), "Start", postfix: nameof(LobbyInterface_Start_Postfix));
                Patch(h, typeof(LobbyInterface), "UpdateUI", postfix: nameof(LobbyInterface_UpdateUI_Postfix));
                Patch(h, typeof(LobbyInterface), "UpdateButtons", postfix: nameof(LobbyInterface_UpdateButtons_Postfix));
                MelonLogger.Msg($"[FullHouse] active - lobby cap raised to {Capacity}.");
            }
            catch (Exception e) { MelonLogger.Error("[FullHouse] patch install failed: " + e); }
        }

        private static void Patch(HarmonyLib.Harmony h, Type type, string method,
            string prefix = null, string postfix = null)
        {
            var target = AccessTools.Method(type, method);
            if (target == null) { MelonLogger.Warning($"[FullHouse] {type.Name}.{method} not found - skipped."); return; }
            h.Patch(target, prefix: Hook(prefix), postfix: Hook(postfix));
        }

        private static HarmonyMethod Hook(string name) =>
            name == null ? null : new HarmonyMethod(typeof(Lobbies).GetMethod(name, AccessTools.all));

        /// <summary>The live Steam lobby service, or null when Steam is down (the game then runs MockLobbyService,
        /// which has no seat array, no member limit and no invite UI - nothing for us to raise).</summary>
        private static SteamLobbyService Service()
        {
            try
            {
                var lobby = Singleton<Lobby>.Instance;
                var svc = lobby?._lobbyService;
                if (svc == null) return null;
#if IL2CPP
                return svc.TryCast<SteamLobbyService>();
#else
                return svc as SteamLobbyService;
#endif
            }
            catch { return null; }
        }

        // ---- seat array ---------------------------------------------------------------------------------

        /// <summary>Grow SteamLobbyService's fixed seat array to <paramref name="target"/>. Idempotent and additive:
        /// only ever grows (so it never fights another cap mod or a host running a bigger lobby), and the copy
        /// preserves existing members - keeping the host at index 0, which is what IsHost reads.</summary>
        private static void EnsurePlayers(SteamLobbyService svc, int target)
        {
            try
            {
                if (svc == null || target < 2) return;
                var cur = svc._players;
                int have = cur != null ? cur.Length : 0;
                if (have >= target) return;
#if IL2CPP
                var grown = new Il2CppStructArray<CSteamID>(target);
#else
                var grown = new CSteamID[target];
#endif
                for (int i = 0; i < have; i++) grown[i] = cur[i];
                svc._players = grown;
            }
            catch (Exception e) { MelonLogger.Warning("[FullHouse] resizing the lobby seat array failed: " + e.Message); }
        }

        private static void Service_Initialize_Postfix(SteamLobbyService __instance)
        {
            EnsurePlayers(__instance, EffectiveCap);
            RaiseTransportCap();   // earliest point the transport usually exists; OnLobbyCreated repeats it
        }

        // ---- the network transport's own client limit ----------------------------------------------------

        /// <summary>
        /// Raise the network transport's client limit to match the lobby cap. Seats in the Steam lobby are only
        /// half the story - the transport keeps its own limit, and a lobby with more seats than the transport
        /// accepts is one that players can enter but not connect into.
        /// <para>
        /// Written as a plain field assignment, called from hooks that already run before a session starts.
        /// Do NOT Harmony-patch <c>FishySteamworks.StartConnection</c> to do this instead: a detour on that
        /// method terminates the process with an access violation (0xc0000005) the moment the server starts,
        /// regardless of what the patch body does. Setting the field beforehand is enough, because
        /// StartConnection reads it when it runs.
        /// </para>
        /// </summary>
        private static void RaiseTransportCap()
        {
            try
            {
                var fishy = UnityEngine.Object.FindObjectOfType<FishyTransport>();
                if (fishy == null) return;                    // transport not spawned yet - a later call gets it
                int cap = EffectiveCap;
                ushort had = fishy._maximumClients;
                if (had >= cap) return;                       // already raised (or another cap mod got there first)
                fishy._maximumClients = (ushort)cap;
                MelonLogger.Msg($"[FullHouse] transport client limit {had} -> {cap}.");
            }
            catch (Exception e) { MelonLogger.Warning("[FullHouse] raising the transport client limit failed: " + e.Message); }
        }

        /// <summary>UpdateLobbyMembers writes <c>_players[j]</c> for every Steam lobby member with no bounds check,
        /// so a fifth member throws IndexOutOfRange inside a Steam callback and takes the session down. This is the
        /// only writer of the array, so guarding here is both sufficient and always in time. Size to the real member
        /// count as well as the cap, in case we joined a lobby larger than our own setting before adopting its cap.</summary>
        private static void Service_UpdateLobbyMembers_Prefix(SteamLobbyService __instance)
        {
            try
            {
                int members = 0;
                try
                {
                    ulong id = __instance != null ? __instance._lobbyID : 0UL;
                    if (id != 0UL) members = SteamMatchmaking.GetNumLobbyMembers(new CSteamID(id));
                }
                catch { }
                EnsurePlayers(__instance, Math.Max(EffectiveCap, members));
            }
            catch (Exception e) { MelonLogger.Warning("[FullHouse] seat guard failed: " + e.Message); }
        }

        // ---- Steam member limit -------------------------------------------------------------------------

        /// <summary>Lobby.CreateLobby() asks for a hard-coded 4. Raise the request itself rather than replacing the
        /// call, so the lobby is born at the right size instead of being resized a frame later. Only ever raises.</summary>
        private static void Service_CreateLobby_Prefix(ref int maxPlayers)
        {
            int cap = EffectiveCap;
            if (maxPlayers < cap) maxPlayers = cap;
        }

        /// <summary>After the lobby exists, raise the Steam member limit to the cap (never replace CreateLobby).
        /// Only ever raises, so a host that deliberately set a smaller per-lobby limit afterwards (e.g. SideHustle's
        /// host slider) still wins.</summary>
        private static void Service_OnLobbyCreated_Postfix(LobbyCreated_t result)
        {
            try
            {
                if (result.m_eResult != EResult.k_EResultOK) return;
                RaiseTransportCap();   // we are about to host: the transport must accept as many as the lobby seats
                CSteamID sid = (CSteamID)result.m_ulSteamIDLobby;
                int cap = EffectiveCap;
                if (SteamMatchmaking.GetLobbyMemberLimit(sid) < cap)
                    SteamMatchmaking.SetLobbyMemberLimit(sid, cap);
                // Advertise the limit Steam actually accepted, not the requested one: if SetLobbyMemberLimit was
                // rejected (host not owner yet, Steam not ready) the real lobby stays smaller, and telling clients a
                // larger cap would let them grow their seats and invite for seats Steam then refuses to fill.
                int real = SteamMatchmaking.GetLobbyMemberLimit(sid);
                if (real < cap) MelonLogger.Warning($"[FullHouse] Steam kept the lobby member limit at {real} (requested {cap}); advertising {real}.");
                SteamMatchmaking.SetLobbyData(sid, "max_players", real.ToString());
                SteamMatchmaking.SetLobbyData(sid, "num_slots", real.ToString());
            }
            catch (Exception e) { MelonLogger.Warning("[FullHouse] OnLobbyCreated failed: " + e.Message); }
        }

        /// <summary>Client-side host sync: on entering a lobby, adopt the host's advertised cap (the "max_players"
        /// lobby data the host writes in OnLobbyCreated) so a client configured smaller than the host still seats
        /// everyone the host admits. Grows the seat array and the UI to the new effective cap. Only ever grows; runs
        /// on the host too (adopts its own value, a no-op). Vanilla bails out of OnLobbyEntered on a version mismatch
        /// AFTER calling LeaveLobby, and this postfix still runs - hence the "did we actually stay" check.</summary>
        private static void Service_OnLobbyEntered_Postfix(SteamLobbyService __instance, LobbyEnter_t result)
        {
            try
            {
                if (__instance == null || __instance._lobbyID == 0UL) return;   // bounced (version mismatch) - nothing joined
                CSteamID sid = (CSteamID)result.m_ulSteamIDLobby;
                int hostCap = 0;
                int.TryParse(SteamMatchmaking.GetLobbyData(sid, "max_players"), out hostCap);

                // "max_players" is just a string the host wrote - it is not authority. Believe Steam instead: the real
                // member limit is what the lobby will actually admit, and it bounds how many seats and UI slots are
                // worth building. Without this a host advertising 250 would have every client allocate 250 seats and
                // clone ~246 lobby rows for members Steam would never let in.
                int steamLimit = SteamMatchmaking.GetLobbyMemberLimit(sid);
                if (steamLimit > 0 && hostCap > steamLimit) hostCap = steamLimit;

                if (hostCap <= _hostCap) return;               // nothing new to adopt
                if (hostCap > SafeMax)
                    MelonLogger.Warning($"[FullHouse] this lobby seats {hostCap}, past the tested maximum of {SafeMax} - expect instability.");
                _hostCap = hostCap;
                int target = EffectiveCap;
                EnsurePlayers(__instance, target);             // the array must fit before the next member update runs
                EnsureSlots(Singleton<LobbyInterface>.Instance, target);
                MelonLogger.Msg($"[FullHouse] adopted host lobby cap {target}.");
            }
            catch (Exception e) { MelonLogger.Warning("[FullHouse] host-cap sync failed: " + e.Message); }
        }

        /// <summary>Leaving a lobby drops the host's cap again. Without this, one visit to a 32-seat lobby would keep
        /// raising the seat count, the invite gate and the UI of every lobby we host afterwards - the adoption is
        /// meant to last as long as we are in THAT host's lobby, not for the rest of the session.</summary>
        private static void Service_LeaveLobby_Postfix()
        {
            if (_hostCap == 0) return;
            _hostCap = 0;
            MelonLogger.Msg($"[FullHouse] left the lobby - back to the local cap of {Capacity}.");
        }

        // ---- invite gate --------------------------------------------------------------------------------

        /// <summary>Vanilla OpenInviteUI refuses to open the Steam overlay once the lobby holds 4 members - a
        /// hard-coded literal, and under IL2CPP there is no IL to transpile. Replace the method with the same body
        /// measured against the effective cap. Mirrors vanilla exactly otherwise, including the fire-and-forget
        /// CreateLobby when we are not in a lobby yet (which our CreateLobby prefix sizes correctly).</summary>
        private static bool Service_OpenInviteUI_Prefix(SteamLobbyService __instance)
        {
            try
            {
                if (__instance == null) return true;
                if (__instance._lobbyID == 0UL) { __instance.CreateLobby(EffectiveCap); return false; }
                CSteamID sid = new CSteamID(__instance._lobbyID);

                // Gate on whichever is smaller: our cap, or the limit Steam actually holds this lobby to. They differ
                // whenever Steam refused the resize, or a host lowered the per-lobby limit afterwards - and inviting
                // past the real limit only produces invitations Steam will not admit.
                int gate = EffectiveCap;
                int steamLimit = SteamMatchmaking.GetLobbyMemberLimit(sid);
                if (steamLimit > 0 && steamLimit < gate) gate = steamLimit;

                if (SteamMatchmaking.GetNumLobbyMembers(sid) >= gate)
                {
                    MelonLogger.Warning($"[FullHouse] lobby already at max capacity ({gate}).");
                    return false;
                }
                SteamFriends.ActivateGameOverlayInviteDialog(sid);
                return false;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[FullHouse] invite gate failed, falling back to vanilla: " + e.Message);
                return true;
            }
        }

        // ---- lobby UI -----------------------------------------------------------------------------------

        // LobbyInterface is a plain Singleton since 0.4.6f11 (it used to be a PersistentSingleton), so it is rebuilt
        // per scene and the slot cloning has to re-run for every instance. Start is that per-instance hook.
        private static void LobbyInterface_Start_Postfix(LobbyInterface __instance)
        {
            try { MelonCoroutines.Start(BuildUi(__instance)); }
            catch (Exception e) { MelonLogger.Warning("[FullHouse] UI coroutine start failed: " + e.Message); }
        }

        /// <summary>Vanilla UpdateUI writes the title as "Lobby (n/4)". Re-apply it with the effective cap. This runs
        /// on every lobby change (UpdateUI is subscribed to Lobby.OnLobbyChange), which is why no separate hook on
        /// that event is needed any more.</summary>
        private static void LobbyInterface_UpdateUI_Postfix(LobbyInterface __instance)
        {
            try
            {
                var lobby = Singleton<Lobby>.Instance;
                int count = lobby != null ? lobby.PlayerCount : 0;
                if (__instance?.LobbyTitle != null)
                    __instance.LobbyTitle.text = "Lobby (" + count + "/" + EffectiveCap + ")";
            }
            catch (Exception e) { MelonLogger.Warning("[FullHouse] title sync failed: " + e.Message); }
        }

        /// <summary>Vanilla hides the invite button once PlayerCount reaches 4. Re-decide against the effective cap.
        /// Runs at High priority so SideHustle's "any member may invite" postfix (Low) still gets the last word.</summary>
        [HarmonyPriority(Priority.High)]
        private static void LobbyInterface_UpdateButtons_Postfix(LobbyInterface __instance)
        {
            try
            {
                var lobby = Singleton<Lobby>.Instance;
                if (lobby == null || __instance?.InviteButton == null) return;
                __instance.InviteButton.gameObject.SetActive(lobby.IsHost && lobby.PlayerCount < EffectiveCap);
            }
            catch (Exception e) { MelonLogger.Warning("[FullHouse] invite button sync failed: " + e.Message); }
        }

        /// <summary>Wait for the Lobby singleton, defensively grow the seat array, then clone the lobby's slot
        /// template up to the effective cap.</summary>
        private static IEnumerator BuildUi(LobbyInterface ui)
        {
            while (Singleton<Lobby>.Instance == null) yield return null;

            bool steamReady = true;
            try { steamReady = SteamManager.Initialized; } catch { }
            if (!steamReady) yield break;   // MockLobbyService - no seats, no members, nothing to grow

            EnsurePlayers(Service(), EffectiveCap);
            EnsureSlots(ui, EffectiveCap);
        }

        /// <summary>Clone the lobby slot template up to <paramref name="target"/> TOTAL slots (counting whatever is
        /// already there, so it never double-clones when another cap mod or an earlier pass added some) and rebuild
        /// PlayerSlots. Idempotent - safe to call again when the effective cap grows (e.g. a client adopting a bigger
        /// host cap). Clones PlayerSlots[0] rather than a guessed child index, so every clone is guaranteed to carry
        /// the "Frame/Avatar" child that DisplayPlayer dereferences.</summary>
        private static void EnsureSlots(LobbyInterface ui, int target)
        {
            if (ui == null) return;
            try
            {
                var slots = ui.PlayerSlots;
                if (slots == null || slots.Length == 0) { MelonLogger.Warning("[FullHouse] lobby PlayerSlots not found."); return; }
                int have = slots.Length;
                if (have >= target)
                {
                    RefreshPlayers(ui);
                    return;
                }

                var template = slots[have - 1];
                var parent = template.parent;
                var grown = new RectTransform[target];
                for (int i = 0; i < have; i++) grown[i] = slots[i];
                for (int i = have; i < target; i++)
                {
                    var clone = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
                    clone.name = template.gameObject.name + " (" + i + ")";
                    grown[i] = clone.GetComponent<RectTransform>();
                }
                ui.PlayerSlots = grown;

                // A freshly-cloned slot defaults to ACTIVE. Vanilla only hides an empty seat inside UpdatePlayers
                // (ClearPlayer -> SetActive(false)), and that only re-runs on a lobby change - so when the panel opens
                // with members already seated, the new empty clones would linger as a blank strip. Refresh once now.
                RefreshPlayers(ui);
            }
            catch (Exception e) { MelonLogger.Warning("[FullHouse] building lobby UI failed: " + e.Message); }
        }

        private static void RefreshPlayers(LobbyInterface ui)
        {
#if IL2CPP
            try { ui.UpdatePlayers(); } catch { }
#else
            try { AccessTools.Method(typeof(LobbyInterface), "UpdatePlayers")?.Invoke(ui, null); } catch { }
#endif
        }
    }
}
