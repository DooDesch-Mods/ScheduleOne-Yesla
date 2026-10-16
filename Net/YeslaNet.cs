using System;
using System.Linq;
using System.Reflection;
using Il2CppFishNet;
using GameQuestManager = Il2CppScheduleOne.Quests.QuestManager;
using EQuestState = Il2CppScheduleOne.Quests.EQuestState;

namespace Yesla.Net
{
    /// <summary>
    /// Yesla's co-op channel. Messages ride two vanilla quest RPCs with a private "YSLA|" guid the vanilla handlers
    /// never see: client -> host on <c>SendQuestState</c> (a ServerRpc), host -> everyone on
    /// <c>ReceiveQuestState</c> (an ObserversRpc). The same carrier RVRepairVan uses; no custom FishNet serializer.
    ///
    /// On a listen server the host also gets a client's message through the ReceiveQuestState relay, so both
    /// prefixes route by role: the host treats every message as a request, a client as state.
    ///
    /// Single player is a host with no clients; every send is then handled locally and nothing touches the wire.
    /// </summary>
    internal static class YeslaNet
    {
        private const string Prefix = "YSLA|";

        /// <summary>Host: a request arrived (from a client, or from the host's own player).</summary>
        internal static Action<Msg> OnRequest;
        /// <summary>Client: state from the host arrived.</summary>
        internal static Action<Msg> OnState;

        internal static bool IsHost
        {
            get
            {
                try { return InstanceFinder.IsServer; }
                catch (Exception e) { Core.Log.Warning("[Net] could not read the server role: " + e.Message); return false; }
            }
        }

        /// <summary>A request to the host. On the host it is handled right here, without a round trip.</summary>
        internal static void ToHost(string op, params string[] args)
        {
            var m = new Msg(op, args);
            if (IsHost)
            {
                Dispatch(OnRequest, m, "request");
                return;
            }
            try
            {
                var qm = GameQuestManager.Instance;
                if (qm == null) { Core.Log.Warning("[Net] no QuestManager - request " + m + " dropped."); return; }
                qm.SendQuestState(m.Encode(), EQuestState.Inactive);
                Core.Log.Msg("[Net] -> host " + m);
            }
            catch (Exception e) { Core.Log.Error("[Net] sending " + m + " to the host failed: " + e.Message); }
        }

        /// <summary>Host only: state for every client. The host applies it to itself as well.</summary>
        internal static void ToAll(string op, params string[] args)
        {
            var m = new Msg(op, args);
            if (!IsHost) { Core.Log.Warning("[Net] a client tried to broadcast " + m + " - only the host does."); return; }
            Dispatch(OnState, m, "state");
            try
            {
                var qm = GameQuestManager.Instance;
                if (qm == null) { Core.Log.Warning("[Net] no QuestManager - broadcast " + m + " reaches no client."); return; }
                qm.ReceiveQuestState(null, m.Encode(), EQuestState.Inactive);
            }
            catch (Exception e) { Core.Log.Error("[Net] broadcasting " + m + " failed: " + e.Message); }
        }

        private static void Dispatch(Action<Msg> handler, Msg m, string what)
        {
            if (handler == null) { Core.Log.Warning("[Net] no handler for " + what + " " + m + "."); return; }
            try { handler(m); }
            catch (Exception e) { Core.Log.Error("[Net] handling " + what + " " + m + " failed: " + e); }
        }

        internal static void Apply(HarmonyLib.Harmony h)
        {
            Patch(h, "RpcLogic___SendQuestState", nameof(SendPrefix));
            Patch(h, "RpcLogic___ReceiveQuestState", nameof(ReceivePrefix));
        }

        private static void Patch(HarmonyLib.Harmony h, string namePrefix, string prefix)
        {
            try
            {
                MethodInfo target = typeof(GameQuestManager)
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name.StartsWith(namePrefix, StringComparison.Ordinal));
                if (target == null) { Core.Log.Error("[Net] " + namePrefix + " not found - Yesla co-op features stay local."); return; }
                h.Patch(target, prefix: new HarmonyLib.HarmonyMethod(typeof(YeslaNet).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)));
                Core.Log.Msg("[Net] carrier patched: " + target.Name + ".");
            }
            catch (Exception e) { Core.Log.Error("[Net] patching " + namePrefix + " failed - Yesla co-op features stay local: " + e.Message); }
        }

        // Host: RpcLogic___SendQuestState(string guid, EQuestState state).
        private static bool SendPrefix(string __0)
        {
            if (!Msg.TryDecode(__0, out Msg m)) return true;
            // On the sending client this is the local run of its own request; only the host acts on it.
            if (IsHost && m.IsRequest) Dispatch(OnRequest, m, "request");
            return false;
        }

        // Everyone: RpcLogic___ReceiveQuestState(NetworkConnection conn, string guid, EQuestState state).
        private static bool ReceivePrefix(string __1)
        {
            if (!Msg.TryDecode(__1, out Msg m)) return true;
            // The host already applied its own broadcast in ToAll; what reaches it here is a relayed client request.
            if (IsHost)
            {
                if (m.IsRequest) Dispatch(OnRequest, m, "request");
                return false;
            }
            if (!m.IsRequest) Dispatch(OnState, m, "state");
            return false;
        }

        /// <summary>One message: an op and its string arguments. Requests start with "req.", state does not.</summary>
        internal sealed class Msg
        {
            internal readonly string Op;
            internal readonly string[] Args;

            internal Msg(string op, string[] args) { Op = op; Args = args ?? new string[0]; }

            internal bool IsRequest => Op.StartsWith("req.", StringComparison.Ordinal);

            internal string Arg(int i) => i < Args.Length ? Args[i] : string.Empty;

            internal string Encode()
            {
                foreach (string a in Args)
                    if (a != null && a.IndexOf('|') >= 0) throw new ArgumentException("a Yesla message argument may not contain '|': " + a);
                return Prefix + Op + (Args.Length > 0 ? "|" + string.Join("|", Args) : string.Empty);
            }

            internal static bool TryDecode(string s, out Msg m)
            {
                m = null;
                if (s == null || !s.StartsWith(Prefix, StringComparison.Ordinal)) return false;
                string[] parts = s.Substring(Prefix.Length).Split('|');
                m = new Msg(parts[0], parts.Skip(1).ToArray());
                return true;
            }

            public override string ToString() => Op + "(" + string.Join(", ", Args) + ")";
        }
    }
}
