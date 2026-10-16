using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Il2CppScheduleOne.Persistence;
using Il2CppScheduleOne.Vehicles;
using Yesla.Net;

namespace Yesla.Vehicle
{
    /// <summary>
    /// Who owns which Yesla, and its lock and Sentry Mode switches.
    ///
    /// Vanilla only knows "player owned" for the whole save, not which player bought a car, so Yesla keeps its own
    /// record. The host is the authority: it keys the record by the car's save GUID, writes it next to the save
    /// (<c>Yesla.cars</c> in the save folder) and broadcasts it to every client by the car's network id, because a
    /// client's GUID for the same car is generated locally and never matches.
    ///
    /// A Yesla with no record (bought before Yesla tracked owners) belongs to the host.
    /// </summary>
    internal static class Fleet
    {
        internal sealed class Rec
        {
            internal string Owner = string.Empty;   // Player.PlayerCode
            internal bool Locked;
            internal bool Sentry;
            internal string Plate = string.Empty;        // the owner's own plate, empty for the factory one
            internal string DefaultPlate = string.Empty; // "YESLA" and three digits from the save GUID

            internal string PlateText => Plate.Length > 0 ? Plate : DefaultPlate;

            internal bool Same(Rec o) => o != null && o.Owner == Owner && o.Locked == Locked && o.Sentry == Sentry
                                         && o.Plate == Plate && o.DefaultPlate == DefaultPlate;
        }

        private static Rec NewRec(string guid, string owner) =>
            new Rec { Owner = owner, DefaultPlate = Vehicle.Plate.Default(guid) };

        private const string FileName = "Yesla.cars";
        private const float ScanSeconds = 1f;

        // Host: by save GUID. Everyone: by network object id, as the host last broadcast it.
        private static readonly Dictionary<string, Rec> ByGuid = new Dictionary<string, Rec>();
        private static readonly Dictionary<int, Rec> ById = new Dictionary<int, Rec>();
        private static string _loadedFrom;
        private static float _nextScan;
        private static string _lastBroadcast = string.Empty;
        private static bool _askedForSync;
        private static bool _scanned;

        /// <summary>How long a purchase announcement and a new, unclaimed car wait for each other.</summary>
        private const float ClaimSeconds = 10f;
        private struct Announcement { internal string Buyer; internal float At; }
        private static readonly Queue<Announcement> Pending = new Queue<Announcement>();
        private static readonly Dictionary<string, float> FirstSeen = new Dictionary<string, float>();

        /// <summary>Fires on every peer when the record of any car changed.</summary>
        internal static event Action Changed;

        internal static int Revision { get; private set; }

        internal static Rec For(LandVehicle car)
        {
            if (car == null) return null;
            int id = NetId(car);
            return id >= 0 && ById.TryGetValue(id, out Rec r) ? r : null;
        }

        internal static bool IsMine(LandVehicle car)
        {
            Rec r = For(car);
            string me = MyCode();
            return r != null && me.Length > 0 && r.Owner == me;
        }

        internal static string MyCode()
        {
            Player local = Player.Local;
            return local != null ? local.PlayerCode ?? string.Empty : string.Empty;
        }

        internal static int NetId(LandVehicle car)
        {
            try { return car.NetworkObject != null ? car.NetworkObject.ObjectId : -1; }
            catch (Exception e) { Core.Log.Warning("[Fleet] no network id for " + car.name + ": " + e.Message); return -1; }
        }

        internal static LandVehicle FindById(int id)
        {
            var all = AllVehicles();
            if (all == null) return null;
            for (int i = 0; i < all.Count; i++)
            {
                var v = all[i];
                if (v != null && NetId(v) == id) return v;
            }
            return null;
        }

        /// <summary>Every Yesla the local player owns.</summary>
        internal static List<LandVehicle> MyCars()
        {
            var list = new List<LandVehicle>();
            var all = AllVehicles();
            if (all == null) return list;
            for (int i = 0; i < all.Count; i++)
            {
                var v = all[i];
                if (v != null && v.IsPlayerOwned && Phone.YeslaApp.IsYesla(v) && IsMine(v)) list.Add(v);
            }
            return list;
        }

        internal static Il2CppSystem.Collections.Generic.List<LandVehicle> AllVehicles() =>
            NetworkSingleton<VehicleManager>.InstanceExists ? NetworkSingleton<VehicleManager>.Instance.AllVehicles : null;

        // ---- host --------------------------------------------------------------------------------------------------

        /// <summary>Host: record a purchase. The buyer becomes the owner; lock and Sentry start off.</summary>
        internal static void Claim(LandVehicle car, string owner, string why)
        {
            if (!YeslaNet.IsHost) { Core.Log.Warning("[Fleet] claim on a client ignored - the host records owners."); return; }
            if (car == null || string.IsNullOrEmpty(owner)) { Core.Log.Warning("[Fleet] claim without a car or owner (" + why + ") ignored."); return; }
            string guid = car.GUID.ToString();
            ByGuid[guid] = NewRec(guid, owner);
            Core.Log.Msg("[Fleet] " + car.VehicleName + " " + guid.Substring(0, 6) + " now belongs to " + owner + " (" + why + ").");
            Save();
            Broadcast(force: true);
        }

        /// <summary>Host: a player announced a Yesla purchase; the next new Yesla is theirs.</summary>
        internal static void Announce(string buyer)
        {
            if (string.IsNullOrEmpty(buyer)) { Core.Log.Warning("[Fleet] purchase announcement without a buyer ignored."); return; }
            Pending.Enqueue(new Announcement { Buyer = buyer, At = Time.unscaledTime });
            Core.Log.Msg("[Fleet] " + buyer + " is buying a Yesla - the next new one is theirs.");
            _nextScan = 0f;
        }

        /// <summary>Host: change a switch on a car. Returns why it was refused, or null.</summary>
        internal static string Set(LandVehicle car, string requester, bool? locked, bool? sentry)
        {
            if (!YeslaNet.IsHost) return "only the host changes a car";
            if (!ByGuid.TryGetValue(car.GUID.ToString(), out Rec r)) return "the car has no owner record";
            if (r.Owner != requester) return "not the owner";
            bool chirp = locked.HasValue && locked.Value != r.Locked;
            if (locked.HasValue) r.Locked = locked.Value;
            if (sentry.HasValue) r.Sentry = sentry.Value;
            if (chirp) YeslaNet.ToAll("chirp", NetId(car).ToString(System.Globalization.CultureInfo.InvariantCulture), r.Locked ? "1" : "0");
            Core.Log.Msg("[Fleet] " + car.VehicleName + " " + Short(car) + ": locked=" + r.Locked + " sentry=" + r.Sentry + " (by " + requester + ").");
            Save();
            Broadcast(force: true);
            return null;
        }

        /// <summary>Host: give a car a plate. Empty text puts the factory plate back. Returns why it was refused, or null.</summary>
        internal static string SetPlate(LandVehicle car, string requester, string text)
        {
            if (!YeslaNet.IsHost) return "only the host changes a car";
            if (!ByGuid.TryGetValue(car.GUID.ToString(), out Rec r)) return "the car has no owner record";
            if (r.Owner != requester) return "not the owner";
            r.Plate = Vehicle.Plate.Clean(text);
            Core.Log.Msg("[Fleet] " + car.VehicleName + " " + Short(car) + ": plate '" + r.PlateText + "'" + (r.Plate.Length == 0 ? " (factory)" : string.Empty)
                       + " (by " + requester + ").");
            Save();
            Broadcast(force: true);
            return null;
        }

        /// <summary>Host: the owner record for a car by save GUID, or null.</summary>
        internal static Rec HostRec(LandVehicle car) =>
            car != null && ByGuid.TryGetValue(car.GUID.ToString(), out Rec r) ? r : null;

        internal static void Tick()
        {
#if SNITCH
            using var __perf = Snitch.Api.Profiler.Sample("Yesla.Fleet.Tick");
#endif
            if (!YeslaNet.IsHost)
            {
                // A client that joined late has not seen a broadcast yet; ask once.
                if (!_askedForSync && Player.Local != null)
                {
                    _askedForSync = true;
                    YeslaNet.ToHost("req.sync", MyCode());
                }
                return;
            }

            float now = Time.unscaledTime;
            if (now < _nextScan) return;
            _nextScan = now + ScanSeconds;

            EnsureLoaded();
            var all = AllVehicles();
            if (all == null) return;
            string host = MyCode();
            if (host.Length == 0) return;
            while (Pending.Count > 0 && now - Pending.Peek().At > ClaimSeconds)
            {
                var stale = Pending.Dequeue();
                Core.Log.Warning("[Fleet] the Yesla purchase announced by " + stale.Buyer + " never showed up as a car - announcement dropped.");
            }
            for (int i = 0; i < all.Count; i++)
            {
                var v = all[i];
                if (v == null || !v.IsPlayerOwned || !Phone.YeslaApp.IsYesla(v)) continue;
                string guid = v.GUID.ToString();
                if (ByGuid.ContainsKey(guid)) continue;

                if (Pending.Count > 0)
                {
                    Claim(v, Pending.Dequeue().Buyer, "bought at the dealership");
                    continue;
                }
                // A car that appears after the first scan was just bought; its buyer's announcement may still be on
                // the way. Cars already there at the first scan came from the save.
                if (_scanned)
                {
                    if (!FirstSeen.TryGetValue(guid, out float seen)) { FirstSeen[guid] = now; continue; }
                    if (now - seen < ClaimSeconds) continue;
                }
                ByGuid[guid] = NewRec(guid, host);
                Core.Log.Msg("[Fleet] " + v.VehicleName + " " + guid.Substring(0, 6) + " had no owner record - it belongs to the host (" + host + ").");
                Save();
            }
            _scanned = true;
            Broadcast(force: false);
        }

        /// <summary>Host: send the current records to everyone when they changed (or when forced).</summary>
        internal static void Broadcast(bool force)
        {
            var sb = new StringBuilder();
            var all = AllVehicles();
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    var v = all[i];
                    if (v == null || !Phone.YeslaApp.IsYesla(v)) continue;
                    if (!ByGuid.TryGetValue(v.GUID.ToString(), out Rec r)) continue;
                    if (sb.Length > 0) sb.Append(';');
                    sb.Append(NetId(v)).Append(',').Append(r.Owner).Append(',').Append(r.Locked ? 1 : 0).Append(',').Append(r.Sentry ? 1 : 0)
                      .Append(',').Append(r.Plate).Append(',').Append(r.DefaultPlate);
                }
            }
            string payload = sb.ToString();
            if (!force && payload == _lastBroadcast) return;
            _lastBroadcast = payload;
            YeslaNet.ToAll("cars", payload);
        }

        /// <summary>Everyone: apply the host's records.</summary>
        internal static void ApplyState(string payload)
        {
            var next = new Dictionary<int, Rec>();
            foreach (string entry in (payload ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] f = entry.Split(',');
                if (f.Length < 4 || !int.TryParse(f[0], out int id)) { Core.Log.Warning("[Fleet] unreadable car record '" + entry + "' skipped."); continue; }
                next[id] = new Rec
                {
                    Owner = f[1], Locked = f[2] == "1", Sentry = f[3] == "1",
                    Plate = f.Length > 4 ? f[4] : string.Empty, DefaultPlate = f.Length > 5 ? f[5] : string.Empty,
                };
            }

            bool changed = next.Count != ById.Count;
            if (!changed)
                foreach (var kv in next)
                    if (!ById.TryGetValue(kv.Key, out Rec old) || !old.Same(kv.Value)) { changed = true; break; }
            if (!changed) return;

            ById.Clear();
            foreach (var kv in next) ById[kv.Key] = kv.Value;
            Revision++;
            Core.Log.Msg("[Fleet] car records: " + (payload.Length > 0 ? payload : "none") + " (I am " + MyCode() + ").");
            try { Changed?.Invoke(); }
            catch (Exception e) { Core.Log.Error("[Fleet] a change listener failed: " + e); }
        }

        private static void EnsureLoaded()
        {
            string path = SavePath();
            if (path == null || path == _loadedFrom) return;
            _loadedFrom = path;
            ByGuid.Clear();
            if (!File.Exists(path)) { Core.Log.Msg("[Fleet] no " + FileName + " in this save yet."); return; }
            try
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    string[] f = line.Split('|');
                    if (f.Length < 4 || f[0].Length == 0) continue;
                    Rec r = NewRec(f[0], f[1]);
                    r.Locked = f[2] == "1";
                    r.Sentry = f[3] == "1";
                    r.Plate = f.Length > 4 ? Vehicle.Plate.Clean(f[4]) : string.Empty;
                    ByGuid[f[0]] = r;
                }
                Core.Log.Msg("[Fleet] loaded " + ByGuid.Count + " car record(s) from " + path + ".");
            }
            catch (Exception e) { Core.Log.Error("[Fleet] could not read " + path + ": " + e.Message); }
        }

        private static void Save()
        {
            string path = SavePath();
            if (path == null) { Core.Log.Warning("[Fleet] no save folder - the car records stay in memory."); return; }
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in ByGuid)
                    sb.Append(kv.Key).Append('|').Append(kv.Value.Owner).Append('|').Append(kv.Value.Locked ? 1 : 0).Append('|').Append(kv.Value.Sentry ? 1 : 0)
                      .Append('|').Append(kv.Value.Plate).Append('\n');
                File.WriteAllText(path, sb.ToString());
                _loadedFrom = path;
            }
            catch (Exception e) { Core.Log.Error("[Fleet] could not write " + path + ": " + e.Message); }
        }

        private static string SavePath()
        {
            if (!Singleton<LoadManager>.InstanceExists) return null;
            string folder = Singleton<LoadManager>.Instance.LoadedGameFolderPath;
            return string.IsNullOrEmpty(folder) || !Directory.Exists(folder) ? null : Path.Combine(folder, FileName);
        }

        private static string Short(LandVehicle car) => car.GUID.ToString().Substring(0, 6);

        internal static string Describe()
        {
            var sb = new StringBuilder("fleet me=" + MyCode() + " host=" + YeslaNet.IsHost);
            foreach (var kv in ById)
                sb.Append(" [#").Append(kv.Key).Append(" owner=").Append(kv.Value.Owner).Append(" locked=").Append(kv.Value.Locked).Append(" sentry=").Append(kv.Value.Sentry).Append(" plate=").Append(kv.Value.PlateText).Append(']');
            return sb.ToString();
        }

        internal static string Describe(LandVehicle car)
        {
            if (car == null) return "no car";
            Rec r = For(car);
            return car.VehicleName + " #" + NetId(car) + " owner=" + (r?.Owner ?? "unknown") + " me=" + MyCode()
                + (r != null ? " locked=" + r.Locked + " sentry=" + r.Sentry + " plate='" + r.PlateText + "'" : string.Empty);
        }

        internal static void Reset()
        {
            ByGuid.Clear();
            ById.Clear();
            _loadedFrom = null;
            _lastBroadcast = string.Empty;
            _askedForSync = false;
            _scanned = false;
            Pending.Clear();
            FirstSeen.Clear();
            _nextScan = 0f;
        }
    }
}
