// FullHouse - self-contained "raise the co-op lobby cap" engine for Schedule I (MelonLoader).
//
// One file, two uses:
//   * Standalone: build with FULLHOUSE_STANDALONE defined -> a drop-in MelonMod (FullHouse.dll) that
//     raises the cap on its own.
//   * Embedded: linked as source into another mod (e.g. SideHustle via Workspace/build/FullHouse.props);
//     the host calls FullHouse.Install() and gets native bigger lobbies with no external dependency.
//
// It targets the game's own Lobby / LobbyInterface (never the Steam API), following the SOTA recipe in
// Workspace/docs/MultiplayerCap/SOTA-MAX-PLAYERS.md:
//   - resize the fixed Lobby.Players[4] array (the client-side seat store),
//   - raise the Steam member limit post-creation via SetLobbyMemberLimit (never replace CreateLobby),
//   - raise the invite gate by transpiling the single literal 4 -> the configured cap,
//   - clone the lobby UI slots and keep the "/N" title in sync.
// All patches are idempotent and additive (only ever grow, never skip the original), so it coexists with
// other cap mods (e.g. BiggerLobbies): the highest cap wins and nothing conflicts. A named-GameObject
// single-flight guard means only one loaded copy installs the patches.
//
// The class is INTERNAL so it can be compiled into several assemblies without a CS0436 clash.

#if IL2CPP
using Il2CppScheduleOne.Networking;
using Il2CppScheduleOne.UI.Multiplayer;
using Il2CppScheduleOne.DevUtilities;
using Il2CppSteamworks;
using Il2Cpp;                       // SteamManager (global-namespace Steamworks.NET helper)
#else
using ScheduleOne.Networking;
using ScheduleOne.UI.Multiplayer;
using ScheduleOne.DevUtilities;
using Steamworks;
#endif
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

#if FULLHOUSE_STANDALONE
[assembly: MelonInfo(typeof(DooDesch.FullHouse.Core), "FullHouse", "1.0.0", "DooDesch", "https://github.com/DooDesch-Mods/ScheduleOne-FullHouse")]
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

        /// <summary>Backing value the invite-gate transpiler loads (Ldsfld) in place of the literal 4. Must be a
        /// field, not a property, so the transpiler can emit a direct field load. Tracks <see cref="EffectiveCap"/>.</summary>
        internal static int PatchCap = DefaultCapacity;

        /// <summary>The host's advertised cap, learned from the lobby's "max_players" data when we join. Lets a
        /// client configured smaller than the host still seat everyone the host admits. Only ever grows.</summary>
        private static int _hostCap;

        /// <summary>The cap actually applied on this client - the larger of our own setting and the host's, clamped
        /// to the hard ceiling. This is what the array, invite gate and UI use, so a client adapts up to its host.</summary>
        internal static int EffectiveCap => Math.Min(HardMax, Math.Max(Capacity, _hostCap));

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

            PatchCap = Capacity;
            if (PatchCap > SafeMax)
                MelonLogger.Warning($"[FullHouse] capacity {PatchCap} exceeds the tested maximum of {SafeMax} - expect instability.");

            try
            {
                var h = new HarmonyLib.Harmony("com.doodesch.fullhouse");
                Patch(h, typeof(Lobby), "Start", postfix: nameof(Lobby_Start_Postfix));
                Patch(h, typeof(Lobby), "OnLobbyCreated", postfix: nameof(Lobby_OnLobbyCreated_Postfix));
                Patch(h, typeof(Lobby), "OnLobbyEntered", postfix: nameof(Lobby_OnLobbyEntered_Postfix));
                Patch(h, typeof(Lobby), "TryOpenInviteInterface", transpiler: nameof(CapLiteralTranspiler));
                Patch(h, typeof(LobbyInterface), "UpdateButtons", transpiler: nameof(CapLiteralTranspiler));
                Patch(h, typeof(LobbyInterface), "Awake", postfix: nameof(LobbyInterface_Awake_Postfix));
                Patch(h, typeof(LobbyInterface), "UpdatePlayers", prefix: nameof(LobbyInterface_UpdatePlayers_Prefix));
                MelonLogger.Msg($"[FullHouse] active - lobby cap raised to {PatchCap}.");
            }
            catch (Exception e) { MelonLogger.Error("[FullHouse] patch install failed: " + e); }
        }

        private static void Patch(HarmonyLib.Harmony h, Type type, string method,
            string prefix = null, string postfix = null, string transpiler = null)
        {
            var target = AccessTools.Method(type, method);
            if (target == null) { MelonLogger.Warning($"[FullHouse] {type.Name}.{method} not found - skipped."); return; }
            h.Patch(target, prefix: Hook(prefix), postfix: Hook(postfix), transpiler: Hook(transpiler));
        }

        private static HarmonyMethod Hook(string name) =>
            name == null ? null : new HarmonyMethod(typeof(Lobbies).GetMethod(name, AccessTools.all));

        // ---- seat array + Steam member limit ------------------------------------------------------------

        private static void Lobby_Start_Postfix(Lobby __instance) => GrowPlayers(__instance, EffectiveCap);

        /// <summary>Grow the fixed Lobby.Players array to <paramref name="target"/>. Idempotent and additive: only
        /// ever grows (so it never fights another cap mod or a host running a bigger lobby), and Array.Copy preserves
        /// the existing members - keeping the host at index 0 (Lobby.IsHost reads Players[0]).</summary>
        internal static void GrowPlayers(Lobby lobby, int target)
        {
            try
            {
                if (lobby == null || lobby.Players == null) return;
                if (lobby.Players.Length >= target) return;
                var grown = new CSteamID[target];
                Array.Copy(lobby.Players, grown, Math.Min(lobby.Players.Length, target));
                lobby.Players = grown;
            }
            catch (Exception e) { MelonLogger.Warning("[FullHouse] resizing Lobby.Players failed: " + e.Message); }
        }

        /// <summary>After the lobby exists, raise the Steam member limit to the cap (the SOTA path - never replace
        /// CreateLobby). Only ever raises, so a host that deliberately set a smaller per-lobby limit afterwards
        /// (e.g. SideHustle's host slider) still wins.</summary>
        private static void Lobby_OnLobbyCreated_Postfix(LobbyCreated_t result)
        {
            try
            {
                if (result.m_eResult != EResult.k_EResultOK) return;
                CSteamID sid = (CSteamID)result.m_ulSteamIDLobby;
                int cap = Capacity;
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
        /// everyone the host admits - preventing the Players[] overflow. Grows the array, the invite-gate value and
        /// the UI to the new effective cap. Only ever grows; runs on the host too (adopts its own value, a no-op).</summary>
        private static void Lobby_OnLobbyEntered_Postfix(LobbyEnter_t result)
        {
            try
            {
                CSteamID sid = (CSteamID)result.m_ulSteamIDLobby;
                int hostCap = 0;
                int.TryParse(SteamMatchmaking.GetLobbyData(sid, "max_players"), out hostCap);
                if (hostCap <= _hostCap) return;               // nothing new to adopt
                _hostCap = hostCap;
                int target = EffectiveCap;
                if (target > PatchCap) PatchCap = target;      // keep the invite-gate transpiler in step
                var lobby = Singleton<Lobby>.Instance;
                if (lobby != null) GrowPlayers(lobby, target); // the array must fit before UpdateLobbyMembers runs
                EnsureSlots(Singleton<LobbyInterface>.Instance, target);
                MelonLogger.Msg($"[FullHouse] adopted host lobby cap {target}.");
            }
            catch (Exception e) { MelonLogger.Warning("[FullHouse] host-cap sync failed: " + e.Message); }
        }

        // ---- invite gate --------------------------------------------------------------------------------

        /// <summary>Replace the single literal 4 (the "&gt;= 4" / "&lt; 4" capacity check) with the configured cap.
        /// Only applied to TryOpenInviteInterface and UpdateButtons, whose sole 4 IS the cap - never blanket-applied
        /// (LobbyInterface's avatar RGBA math also uses a literal 4). A transpiler (not a prefix-skip) so it composes
        /// with another cap mod's prefix instead of double-handling the invite.</summary>
        private static IEnumerable<CodeInstruction> CapLiteralTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var capField = AccessTools.Field(typeof(Lobbies), nameof(PatchCap));
            foreach (var ins in instructions)
            {
                if (ins.opcode == OpCodes.Ldc_I4_4)
                    yield return new CodeInstruction(OpCodes.Ldsfld, capField);
                else
                    yield return ins;
            }
        }

        // ---- lobby UI -----------------------------------------------------------------------------------

        private static void LobbyInterface_Awake_Postfix(LobbyInterface __instance)
        {
            try { MelonCoroutines.Start(BuildUi(__instance)); }
            catch (Exception e) { MelonLogger.Warning("[FullHouse] UI coroutine start failed: " + e.Message); }
        }

        /// <summary>Vanilla UpdatePlayers loops <c>for i in 0..PlayerSlots.Length</c> and indexes
        /// <c>Lobby.Players[i]</c>. Once the UI slots are grown to the cap, a join/rejoin that momentarily leaves the
        /// seat array at the vanilla size 4 (the game reallocates it on some member changes) makes that index
        /// overflow and throws an IndexOutOfRangeException on the IL2CPP trampoline - taking down the whole session.
        /// Guarantee the seat array is at least as large as the UI before the vanilla loop runs. Idempotent (grows
        /// only), and the extra slots read as CSteamID.Nil so vanilla just clears them.</summary>
        private static void LobbyInterface_UpdatePlayers_Prefix(LobbyInterface __instance)
        {
            try
            {
                if (__instance?.PlayerSlots == null) return;
                // Vanilla indexes __instance.Lobby.Players (the interface's OWN field), not the Lobby singleton, so
                // grow exactly that array. They are normally the same object, but a stale UI instance during a
                // leave/rejoin can still hold a lobby the singleton no longer points at (or the singleton is null);
                // growing the singleton would then leave vanilla overflowing the array it actually reads.
                var lobby = __instance.Lobby ?? Singleton<Lobby>.Instance;
                if (lobby?.Players == null) return;
                if (lobby.Players.Length < __instance.PlayerSlots.Length)
                    GrowPlayers(lobby, __instance.PlayerSlots.Length);
            }
            catch (Exception e) { MelonLogger.Warning("[FullHouse] UpdatePlayers guard failed: " + e.Message); }
        }

        // Set once: the Lobby singleton persists across scenes, so its onLobbyChange must be wrapped a single time.
        private static bool _titleHooked;

        /// <summary>Wait for the Lobby singleton, defensively grow the seat array, then clone the lobby's slot
        /// template up to <c>cap</c> TOTAL slots (counting whatever is already there, so it never double-clones when
        /// another cap mod added some), rebuild PlayerSlots, and set the "/cap" title.</summary>
        private static IEnumerator BuildUi(LobbyInterface ui)
        {
            while (Singleton<Lobby>.Instance == null) yield return null;
            var lobby = Singleton<Lobby>.Instance;

            GrowPlayers(lobby, EffectiveCap);   // the array must be sized before any member update runs

            bool steamReady = true;
            try { steamReady = SteamManager.Initialized; } catch { }
            if (!steamReady) yield break;

            EnsureSlots(ui, EffectiveCap);

            // Vanilla re-sets the title to ".../4" on every lobby change; wrap onLobbyChange ONCE to re-apply the
            // effective cap after. The Lobby is a persistent singleton that survives scene reloads, so a per-Awake
            // re-wrap would chain wrappers that pile up and reference destroyed UI; the single hook instead resolves
            // the live LobbyInterface each time and reads EffectiveCap live, so a later host-cap sync corrects it too.
            if (!_titleHooked)
            {
                _titleHooked = true;
                try
                {
                    var prev = lobby.onLobbyChange;
#if IL2CPP
                    lobby.onLobbyChange = new System.Action(() =>
                    {
                        try { prev?.Invoke(); } catch { }
                        try { var cur = Singleton<LobbyInterface>.Instance; if (cur != null) cur.LobbyTitle.text = "Lobby (" + lobby.PlayerCount + "/" + EffectiveCap + ")"; } catch { }
                    });
#else
                    lobby.onLobbyChange = () =>
                    {
                        try { prev?.Invoke(); } catch { }
                        try { var cur = Singleton<LobbyInterface>.Instance; if (cur != null) cur.LobbyTitle.text = "Lobby (" + lobby.PlayerCount + "/" + EffectiveCap + ")"; } catch { }
                    };
#endif
                }
                catch (Exception e) { _titleHooked = false; MelonLogger.Warning("[FullHouse] title sync hook failed: " + e.Message); }
            }
        }

        /// <summary>Clone the lobby slot template up to <paramref name="target"/> TOTAL slots (counting whatever is
        /// already there, so it never double-clones when another cap mod or an earlier pass added some), rebuild
        /// PlayerSlots, and set the "/target" title. Idempotent - safe to call again when the effective cap grows
        /// (e.g. a client adopting a bigger host cap).</summary>
        private static void EnsureSlots(LobbyInterface ui, int target)
        {
            if (ui == null) return;
            GridLayoutGroup grid = null;
            try { grid = ui.GetComponentInChildren<GridLayoutGroup>(); } catch { }
            if (grid == null) { MelonLogger.Warning("[FullHouse] lobby GridLayoutGroup not found."); return; }

            try
            {
                var entries = grid.transform;
                // Children: [0] = invite button, [1..] = player slots. Clone slot[1] as the template up to `target`.
                if (entries.childCount > 1)
                {
                    var template = entries.GetChild(1);
                    for (int have = entries.childCount - 1; have < target; have++)
                    {
                        var clone = UnityEngine.Object.Instantiate(template.gameObject, entries);
                        clone.name = template.gameObject.name + " (" + (entries.childCount - 2) + ")";
                    }

                    int slotCount = entries.childCount - 1;
                    var slots = new RectTransform[slotCount];
                    for (int j = 1; j < entries.childCount; j++)
                        slots[j - 1] = entries.GetChild(j).GetComponent<RectTransform>();
                    ui.PlayerSlots = slots;
                }
                var lobby = Singleton<Lobby>.Instance;
                int count = lobby != null ? lobby.PlayerCount : 0;
                ui.LobbyTitle.text = "Lobby (" + count + "/" + target + ")";

                // A freshly-cloned slot defaults to ACTIVE. Vanilla only hides an empty seat inside UpdatePlayers
                // (ClearPlayer -> SetActive(false)), and that only re-runs on a lobby change - so when the panel opens
                // with members already seated, the new empty clones would linger as a blank strip across the top.
                // Refresh once now; the Players array was grown first (BuildUi + the UpdatePlayers prefix), so the
                // vanilla loop covers every slot instead of overflowing at index 4 and leaving the rest visible.
#if IL2CPP
                try { ui.UpdatePlayers(); } catch { }
#else
                try { AccessTools.Method(typeof(LobbyInterface), "UpdatePlayers")?.Invoke(ui, null); } catch { }
#endif
            }
            catch (Exception e) { MelonLogger.Warning("[FullHouse] building lobby UI failed: " + e.Message); }
        }
    }
}
