using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ShreddedBots
{
    /// <summary>
    /// Offline-only: no Epic Online Services, no Discord SDK, no Steam online multiplayer (lobbies/invites/rich-presence join),
    /// no async-multiplayer web notifications. Steam itself (SteamAPI init, achievements, stats, DLC ownership, Steam Input, overlay) is NOT touched.
    ///
    /// The game's online play is EOS-lobby based (NetworkManager : NetworkManagerBase). Skipping EpicHelper.Init keeps EpicHelper.Interface == null,
    /// which makes the EOS tick / lobby / login code short-circuit; every entry point is additionally no-op'd (belt and braces).
    /// Local co-op does not use any of these methods.
    /// </summary>
    public static class OnlineBlock
    {
        private const string Epic = "online-epic", SteamNet = "online-steam", Net = "online-net", Disc = "online-discord";

        public static void Apply(Harmony h)
        {
            // ---- Epic Online Services ---------------------------------------------------------------------------------------
            Type ep = Hooks.T("Paris.Engine.EpicHelper");
            Hooks.Block(Epic, ep, "EpicHelper", "Init", true, "no EOS platform, no EOS login / stats / achievement sync / lobbies / P2P, no Steam encrypted-ticket request");
            foreach (string n in new[] { "Login", "Reauth", "DoLinkAccount", "InternalLogin", "EpicLogin", "LinkEpicAccount", "LogOut" })
                Hooks.Block(Epic, ep, "EpicHelper", n, true, null, optional: n != "Login" && n != "Reauth" && n != "DoLinkAccount" && n != "InternalLogin");

            // ---- Discord SDK (rich presence only; discord_game_sdk native library) -------------------------------------------
            Type dm = Hooks.T("Paris.Engine.DiscordManager");
            foreach (string n in new[] { "Init", "Tick", "SetStatus", "Uninit" })
                Hooks.Block(Disc, dm, "DiscordManager", n, true, "Discord SDK -> no-op (native discord_game_sdk is never loaded)");
            try
            {
                PropertyInfo dis = dm?.GetProperty("DisableDiscord", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (dis != null) dis.SetValue(null, true, null);
                Hooks.Info(Disc, "DiscordManager.DisableDiscord forced true", dis != null && (bool)dis.GetValue(null, null), true);
            }
            catch (Exception ex) { Hooks.Info(Disc, "DiscordManager.DisableDiscord could not be set (" + ex.Message + ")", false, true); }

            // ---- async multiplayer: api.steampowered.com game-notification sessions (embeds a Steam web API key; never logged) ------
            Type am = Hooks.T("Paris.Engine.System.Networking.AsyncMultiplayer");
            if (am == null) Hooks.NotFound(Net, "AsyncMultiplayer", true);
            else
            {
                var names = am.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(m => !m.IsSpecialName && !m.IsGenericMethodDefinition && !m.IsAbstract).Select(m => m.Name).Distinct().ToList();
                if (!names.Contains("CreateSession")) Hooks.NotFound(Net, "AsyncMultiplayer.CreateSession", true);
                foreach (string n in names) Hooks.Block(Net, am, "AsyncMultiplayer", n, true, "Steam web-API session -> no-op");
            }

            // ---- Steam online multiplayer: lobbies, invites, join-from-friend, rich-presence connect string ------------------------
            Type sh = Hooks.T("Paris.Engine.SteamHelper");
            foreach (string n in new[] { "AddFriends", "OnGameLobbyJoinRequested", "JoinLobby", "OnLobbyDataReceived", "OnGameRichPresenceJoinRequested",
                                         "OnLobbyInviteReceived", "OnLobbyCreated", "OnFriendRichPresenceUpdated", "InviteToGame", "SetJoinInfo" })
                Hooks.Block(SteamNet, sh, "SteamHelper", n, true, "Steam lobby / invite / join -> no-op");

            // ---- the network manager itself (EOS-backed; Interface is null so these would be no-ops anyway) -------------------------
            Type nmb = Hooks.T("Paris.Engine.System.Networking.NetworkManagerBase");
            Type nm = Hooks.T("Paris.Engine.System.Networking.NetworkManager");
            foreach (Type t in new[] { nm, nmb })
            {
                string tn = t == nm ? "NetworkManager" : "NetworkManagerBase";
                foreach (string n in new[] { "LogIn", "HostGame", "JoinGame", "FindGames", "FindSpecificLobbyToJoinAndJoin", "FetchFriends", "FetchFriendLobby",
                                             "JoinLobbyInvite", "InternalJoinInvite", "JoinInvite", "InviteNetworkUser", "InviteFriend", "ShowLobbyInviteWindow" })
                    Hooks.Block(Net, t, tn, n, true, "online lobby / invite entry point -> no-op", optional: t == nm || n == "InternalJoinInvite" || n == "JoinInvite");
            }
            if (nm == null) Hooks.NotFound(Net, "NetworkManager", true);
            if (nmb == null) Hooks.NotFound(Net, "NetworkManagerBase", true);

            try { ApplyOfflineGuards(nm); }
            catch (Exception ex) { Log.Exception("net-guard hooks failed (a lobby call could crash when offline)", ex); }
        }

        // ------------------------------------------------------------------------------------------------ offline guards (M2.1)
        // WHY: the vanilla game relies on EpicHelper.Init having run: even when the player is NOT logged in / not in a lobby, the EOS platform object
        // (EpicHelper.Interface) exists and calls such as NetworkManager.ChangeLobbyLocked (CharacterSelect.Init, survival results, GameInfo...) and
        // NetworkManager.Disconnect (-> UnsubscribeFrom*) use it unconditionally. With EpicHelper.Init blocked, Interface is null, so those calls threw
        // NullReferenceException (crash entering Story > Continue). Fix: the EOS-only helpers become no-ops while Interface is null, and a finalizer on every
        // known lobby / invite / P2P method of the Epic network manager swallows (and logs once) a NullReferenceException that was thrown inside
        // NetworkManager / NetworkUserInfoEpic while EOS is not running. Not generic: any other exception, or anything outside these methods, is untouched.
        private const string Guard = "net-guard";
        private static readonly System.Collections.Generic.HashSet<string> _guardLogged = new System.Collections.Generic.HashSet<string>();

        /// <summary>EOS-only helpers of NetworkManager: skipped (not run at all) while EOS is not running; every other lobby / P2P method just gets the NRE finalizer.</summary>
        private static readonly string[] SkipWhenNoEos = { "SetInvitesAllowed", "SetPermissionLevel", "SubscribeToLobbyUpdates", "UnsubscribeFromLobbyUpdates",
                                                          "SubscribeToConnectionRequests", "UnsubscribeFromConnectionRequests" };
        private static readonly string[] FinalizerOnly = { "ChangeLobbyLocked", "ChangeLobbyMaxPlayers", "Disconnect", "PushLobbyExtraInfo", "PushMemberInfo", "ReceivePackets",
                                                          "KickPlayer", "JustEnteredLobby", "AddLocalGuest", "RemoveMember", "SetChannel", "SendPacketTo", "CloseConnection",
                                                          "UpdateLobbyPlayerCount", "ModifyLobby", "UpdateJoinInfo", "LoginResult" };

        private static MethodInfo _ifaceGetter;
        private static bool EosRunning()
        {
            try
            {
                if (_ifaceGetter == null) _ifaceGetter = Hooks.T("Paris.Engine.EpicHelper")?.GetProperty("Interface", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetGetMethod(true);
                return _ifaceGetter != null && _ifaceGetter.Invoke(null, null) != null;
            }
            catch { return true; }   // unknown -> behave like vanilla (do not skip / do not swallow)
        }

        private static void ApplyOfflineGuards(Type nm)
        {
            if (nm == null) { Hooks.NotFound(Guard, "NetworkManager (net-guard)", false); return; }
            foreach (string n in SkipWhenNoEos)
            {
                var ms = Hooks.Find(nm, n);
                if (ms.Count == 0) Hooks.NotFound(Guard, "NetworkManager." + n + " (net-guard skip)", false);
                foreach (MethodBase m in ms)
                    Hooks.Patch(Guard, Hooks.Sig(m) + " -> skipped while EOS is not running (+ NRE finalizer)", m, false, typeof(OnlineBlock), prefix: nameof(GuardSkipPrefix), finalizer: nameof(GuardFinalizer));
            }
            foreach (string n in FinalizerOnly)
            {
                var ms = Hooks.Find(nm, n);
                if (ms.Count == 0) { Hooks.NotFound(Guard, "NetworkManager." + n + " (net-guard)", false); continue; }
                foreach (MethodBase m in ms)
                    Hooks.Patch(Guard, Hooks.Sig(m) + " -> NRE finalizer while EOS is not running", m, false, typeof(OnlineBlock), finalizer: nameof(GuardFinalizer));
            }
            Type ui = Hooks.T("Paris.Engine.System.Networking.NetworkUserInfoEpic");
            foreach (MethodBase m in Hooks.Find(ui, "Invite"))
                Hooks.Patch(Guard, Hooks.Sig(m) + " -> skipped while EOS is not running", m, false, typeof(OnlineBlock), prefix: nameof(GuardSkipPrefix), finalizer: nameof(GuardFinalizer));
        }

        /// <summary>Harmony prefix: false (= skip the original) while EOS is not running.</summary>
        public static bool GuardSkipPrefix(MethodBase __originalMethod)
        {
            if (EosRunning()) return true;
            LogGuardOnce("skip:" + __originalMethod.Name, "[net-guard] " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name + ": skipped (EOS not running, offline mode)");
            return false;
        }

        /// <summary>Harmony finalizer: swallows ONLY a NullReferenceException whose throw site is inside NetworkManager / NetworkUserInfoEpic while EOS is not running.</summary>
        public static Exception GuardFinalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception == null) return null;
            try
            {
                if (__exception is NullReferenceException && !EosRunning())
                {
                    string st = __exception.StackTrace ?? "";
                    int nl = st.IndexOf('\n'); string top = nl > 0 ? st.Substring(0, nl) : st;
                    if (top.Contains("NetworkUserInfoEpic") || (top.Contains("NetworkManager") && !top.Contains("NetworkManagerBase")))   // frame naming differs between runtimes (patched methods show as DMD / _Patch frames)
                    {
                        LogGuardOnce("nre:" + __originalMethod.Name, "[net-guard] " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name + ": swallowed NullReferenceException (EOS not running, offline mode) " + top.Trim());
                        return null;
                    }
                }
            }
            catch { }
            return __exception;
        }

        private static void LogGuardOnce(string key, string msg)
        {
            lock (_guardLogged) { if (!_guardLogged.Add(key)) return; }
            Log.Info(msg);
        }

        /// <summary>First-frame runtime verification (called once from the menu tick): proves the state the patches are supposed to produce.</summary>
        public static void RuntimeCheck()
        {
            try
            {
                Type ep = Hooks.T("Paris.Engine.EpicHelper");
                object iface = ep?.GetProperty("Interface", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null, null)
                               ?? ep?.GetField("Interface", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                Log.Info("[selfcheck] runtime: EpicHelper.Interface is " + (iface == null ? "null (EOS not running) - OK" : "NOT NULL - EOS IS RUNNING, BLOCK FAILED"));
                bool disc = (bool)(Hooks.T("Paris.Engine.DiscordManager")?.GetProperty("DisableDiscord", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null, null) ?? false);
                Log.Info("[selfcheck] runtime: DiscordManager.DisableDiscord = " + disc);
                Type settings = AccessTools.TypeByName("NBug.Settings");
                object dest = settings?.GetProperty("Destinations", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null, null);
                int cnt = dest is System.Collections.ICollection c ? c.Count : -1;
                Log.Info("[selfcheck] runtime: NBug.Settings.Destinations count = " + cnt + (cnt == 0 ? " - OK" : " - UNEXPECTED"));
                Log.Info("[selfcheck] runtime: blocked calls so far: " + Blocks.Summary());
            }
            catch (Exception ex) { Log.Warn("[selfcheck] runtime check failed: " + ex.Message); }
        }
    }
}
