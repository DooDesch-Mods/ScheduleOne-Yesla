using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Chassis.Api;
using Il2CppScheduleOne.Persistence;
using Il2CppScheduleOne.Vehicles;
using Yesla.Net;

namespace Yesla.Vehicle
{
    /// <summary>
    /// The Yesla's own switches: Sentry Mode and the licence plate. Who owns a car and whether it is locked is
    /// Chassis' record (<see cref="ChassisApi.OwnerOf"/>, <see cref="ChassisApi.IsLocked"/>); a <see cref="Rec"/> carries
    /// both as read from Chassis, so callers see one record per car.
    ///
    /// The host is the authority: it keys the record by the car's save GUID, writes it next to the save
    /// (<c>Yesla.cars</c> in the save folder) and broadcasts it to every client by the car's network id, because a
    /// client's GUID for the same car is generated locally and never matches. The file keeps the owner and lock
    /// columns, so a save opened by a Yesla without Chassis keeps them, and Chassis imports them for cars it has no
    /// record of yet (<see cref="ChassisApi.ResolveUnknownOwner"/>).
    /// </summary>
    internal static class Fleet
    {
        internal sealed class Rec
        {
            internal string Owner = string.Empty;   // Player.PlayerCode, from Chassis
            internal bool Locked;                   // from Chassis
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

        /// <summary>Fires on every peer when the record of any car changed, the owner or lock in Chassis included.</summary>
        internal static event Action Changed;

        internal static int Revision { get; private set; }

        /// <summary>Hooks into Chassis' owner records; once per process.</summary>
        internal static void Init()
        {
            var earlier = ChassisApi.ResolveUnknownOwner;
            ChassisApi.ResolveUnknownOwner = lv => Import(lv) ?? earlier?.Invoke(lv);
            ChassisApi.OwnersChanged += () => { Revision++; Raise(); };
            Core.Log.Msg("[Fleet] owners and locks come from Chassis; Yesla.cars records are imported for cars Chassis does not know yet.");
        }

        /// <summary>Host, for Chassis: owner and lock of a Yesla from <c>Yesla.cars</c>, or null.</summary>
        private static OwnerRecord Import(LandVehicle car)
        {
            if (car == null || !Phone.YeslaApp.IsYesla(car)) return null;
            EnsureLoaded();
            if (!ByGuid.TryGetValue(car.GUID.ToString(), out Rec r) || r.Owner.Length == 0) return null;
            Core.Log.Msg("[Fleet] " + car.VehicleName + " " + Short(car) + ": owner " + r.Owner + (r.Locked ? ", locked" : string.Empty) + " handed to Chassis from " + FileName + ".");
            return new OwnerRecord { Owner = r.Owner, Locked = r.Locked };
        }

        internal static Rec For(LandVehicle car)
        {
            if (car == null) return null;
            int id = NetId(car);
            return id >= 0 && ById.TryGetValue(id, out Rec r) ? FromChassis(car, r) : null;
        }

        internal static bool IsMine(LandVehicle car) => car != null && ChassisApi.IsMine(car);

        internal static string MyCode() => ChassisApi.LocalPlayerCode ?? string.Empty;

        private static Rec FromChassis(LandVehicle car, Rec r)
        {
            r.Owner = ChassisApi.OwnerOf(car);
            r.Locked = ChassisApi.IsLocked(car);
            return r;
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

        /// <summary>Host: switch Sentry Mode on a car. Returns why it was refused, or null.</summary>
        internal static string SetSentry(LandVehicle car, string requester, bool on)
        {
            if (!YeslaNet.IsHost) return "only the host changes a car";
            Rec r = HostRec(car);
            if (r == null) return "the car has no record";
            if (r.Owner != requester) return "not the owner";
            r.Sentry = on;
            Core.Log.Msg("[Fleet] " + car.VehicleName + " " + Short(car) + ": sentry=" + r.Sentry + " (by " + requester + ").");
            Save();
            Broadcast(force: true);
            return null;
        }

        /// <summary>Host: give a car a plate. Empty text puts the factory plate back. Returns why it was refused, or null.</summary>
        internal static string SetPlate(LandVehicle car, string requester, string text)
        {
            if (!YeslaNet.IsHost) return "only the host changes a car";
            Rec r = HostRec(car);
            if (r == null) return "the car has no record";
            if (r.Owner != requester) return "not the owner";
            r.Plate = Vehicle.Plate.Clean(text);
            Core.Log.Msg("[Fleet] " + car.VehicleName + " " + Short(car) + ": plate '" + r.PlateText + "'" + (r.Plate.Length == 0 ? " (factory)" : string.Empty)
                       + " (by " + requester + ").");
            Save();
            Broadcast(force: true);
            return null;
        }

        /// <summary>Host: the record for a car by save GUID, with owner and lock from Chassis, or null.</summary>
        internal static Rec HostRec(LandVehicle car) =>
            car != null && ByGuid.TryGetValue(car.GUID.ToString(), out Rec r) ? FromChassis(car, r) : null;

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
            bool dirty = false;
            for (int i = 0; i < all.Count; i++)
            {
                var v = all[i];
                if (v == null || !v.IsPlayerOwned || !Phone.YeslaApp.IsYesla(v)) continue;
                // Chassis decides the owner; a car it has not settled yet (a purchase still on its way) waits.
                string owner = ChassisApi.OwnerOf(v);
                if (owner.Length == 0) continue;
                string guid = v.GUID.ToString();
                bool locked = ChassisApi.IsLocked(v);
                if (!ByGuid.TryGetValue(guid, out Rec r))
                {
                    ByGuid[guid] = r = NewRec(guid, owner);
                    r.Locked = locked;
                    Core.Log.Msg("[Fleet] " + v.VehicleName + " " + Short(v) + " gets a Yesla record, owner " + owner + ".");
                    dirty = true;
                }
                else if (r.Owner != owner || r.Locked != locked)
                {
                    r.Owner = owner;
                    r.Locked = locked;
                    dirty = true;
                }
            }
            if (dirty) Save();
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
                    Rec r = HostRec(v);
                    if (r == null) continue;
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
            Raise();
        }

        private static void Raise()
        {
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
            {
                LandVehicle car = FindById(kv.Key);
                Rec r = car != null ? FromChassis(car, kv.Value) : kv.Value;
                sb.Append(" [#").Append(kv.Key).Append(" owner=").Append(r.Owner).Append(" locked=").Append(r.Locked).Append(" sentry=").Append(r.Sentry).Append(" plate=").Append(r.PlateText).Append(']');
            }
            return sb.ToString();
        }

        internal static string Describe(LandVehicle car)
        {
            if (car == null) return "no car";
            Rec r = For(car);
            return car.VehicleName + " #" + NetId(car) + " owner=" + ChassisApi.OwnerOf(car) + " me=" + MyCode() + " locked=" + ChassisApi.IsLocked(car)
                + (r != null ? " sentry=" + r.Sentry + " plate='" + r.PlateText + "'" : " (no Yesla record yet)");
        }

        internal static void Reset()
        {
            ByGuid.Clear();
            ById.Clear();
            _loadedFrom = null;
            _lastBroadcast = string.Empty;
            _askedForSync = false;
            _nextScan = 0f;
        }
    }
}
