using System.Globalization;
using Il2CppScheduleOne.Vehicles;
using Sideload.Api;
using Yesla.Config;
using Yesla.Driving;
using Yesla.Vehicle;

namespace Yesla.Phone
{
    /// <summary>
    /// The Yesla phone app: a map with the player, the car and the destination, a list of places, and "Send to
    /// car". Rendered by Sideload from <c>Assets/yesla/</c>; without Sideload every call here is a no-op and the
    /// car still drives through the console and the engage key.
    ///
    /// The destination is the local player's: whoever sends it is the one who drives, on their own machine, so a
    /// co-op client needs no network call to use it.
    /// </summary>
    internal static class YeslaApp
    {
        internal const string AppId = "yesla";

        private const float PulseSeconds = 0.25f;
        private const float PlacesSeconds = 5f;

        private static AppHandle _app;
        private static float _nextPulse;
        private static float _nextPlaces;
        private static int _pushedState = int.MinValue;
        private static int _pushedPlaces = int.MinValue;

        internal static void Register()
        {
            _app = Apps.Register(AppId, "Yesla.Assets.yesla", "Yesla", "Yesla");
            _app.OnCall("yesla.state", _ => Guard("state", State))
                .OnCall("yesla.places", _ => Guard("places", PlacesJson))
                .OnCall("yesla.send.place", id => Guard("send.place", () => SendPlace(id)))
                .OnCall("yesla.send.point", xy => Guard("send.point", () => SendPoint(xy)))
                .OnCall("yesla.clear", _ => Guard("clear", Clear));
            Core.Log.Msg(Apps.Available
                ? "[App] registered with Sideload."
                : "[App] Sideload not loaded yet - the Yesla app registration is queued.");
        }

        internal static void Tick()
        {
            if (_app == null) return;
            float now = Time.unscaledTime;
            if (now < _nextPulse) return;
            _nextPulse = now + PulseSeconds;

            MapPicture.Ensure(_app);
            if (!_app.IsOnScreen) return;

            int state = Revision();
            if (state != _pushedState)
            {
                _pushedState = state;
                _app.Emit("yesla.changed", "");
            }

            if (now >= _nextPlaces)
            {
                _nextPlaces = now + PlacesSeconds;
                var list = Places.Collect();
                int hash = 17;
                unchecked { foreach (var p in list) hash = hash * 31 + p.Id.GetHashCode(); }
                if (hash != _pushedPlaces)
                {
                    _pushedPlaces = hash;
                    _app.Emit("yesla.places", "");
                }
            }
        }

        internal static void Reset()
        {
            MapPicture.Reset();
            _pushedState = int.MinValue;
            _pushedPlaces = int.MinValue;
        }

        /// <summary>The Yesla this player is sitting in, the one Assisted Driving steers, or the nearest owned one.</summary>
        internal static LandVehicle MyCar()
        {
            LandVehicle sitting = Autopilot.CurrentCar();
            if (sitting != null && IsYesla(sitting)) return sitting;
            if (Autopilot.DrivenCar != null) return Autopilot.DrivenCar;

            Player local = Player.Local;
            var all = NetworkSingleton<VehicleManager>.InstanceExists ? NetworkSingleton<VehicleManager>.Instance.AllVehicles : null;
            if (local == null || all == null) return null;
            LandVehicle best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < all.Count; i++)
            {
                var v = all[i];
                // IsPlayerOwned is replicated; PlayerOwnedVehicles is only filled on the host.
                if (v == null || !v.IsPlayerOwned || !IsYesla(v)) continue;
                float d = Vector3.Distance(local.transform.position, v.transform.position);
                if (d < bestDist) { bestDist = d; best = v; }
            }
            return best;
        }

        internal static bool IsYesla(LandVehicle v) =>
            v != null && string.Equals(v.VehicleCode, YeslaVehicleType.Code, StringComparison.OrdinalIgnoreCase);

        private static string State()
        {
            var j = new Json().Open();
            j.Prop("image", MapPicture.Ready);
            j.Prop("imageName", MapPicture.ImageName);

            Player local = Player.Local;
            if (local != null && MapGeo.TryNormalise(local.transform.position, out double px, out double py))
                j.Key("player").Open().Prop("x", px).Prop("y", py).Prop("h", local.transform.eulerAngles.y).Close();
            else j.Null("player");

            LandVehicle car = MyCar();
            if (car != null && MapGeo.TryNormalise(car.transform.position, out double cx, out double cy))
            {
                j.Key("car").Open()
                    .Prop("x", cx).Prop("y", cy).Prop("h", car.transform.eulerAngles.y)
                    .Prop("name", car.VehicleName)
                    .Prop("speed", Mathf.Abs(car.Speed_Kmh))
                    .Prop("inside", Autopilot.CurrentCar() != null && Autopilot.CurrentCar().Pointer == car.Pointer)
                    .Prop("distance", local != null ? Vector3.Distance(local.transform.position, car.transform.position) : 0)
                    .Close();
            }
            else j.Null("car");

            if (Autopilot.Destination.HasValue && MapGeo.TryNormalise(Autopilot.Destination.Value, out double dx, out double dy))
            {
                Vector3 from = car != null ? car.transform.position : (local != null ? local.transform.position : Autopilot.Destination.Value);
                j.Key("dest").Open()
                    .Prop("x", dx).Prop("y", dy)
                    .Prop("label", Autopilot.DestinationLabel)
                    .Prop("distance", Vector3.Distance(from, Autopilot.Destination.Value))
                    .Close();
            }
            else j.Null("dest");

            j.Key("drive").Open()
                .Prop("state", Autopilot.State.ToString())
                .Prop("reason", Autopilot.LastReason)
                .Prop("key", Preferences.EngageKey.ToString())
                .Close();

            return j.Close().ToString();
        }

        private static string PlacesJson()
        {
            var list = Places.Collect();
            var j = new Json().OpenArray();
            foreach (var p in list)
            {
                j.Open().Prop("id", p.Id).Prop("label", p.Label).Prop("kind", p.Kind)
                 .Prop("x", p.X).Prop("y", p.Y).Prop("distance", p.Distance).Close();
            }
            return j.CloseArray().ToString();
        }

        private static string SendPlace(string id)
        {
            Place place = Places.Find(id);
            if (place == null) { Core.Log.Warning("[App] send: no place with id '" + id + "'."); return "error:That place is gone."; }
            Autopilot.SetDestination(place.World, place.Label);
            Core.Log.Msg("[App] destination sent: " + place.Label + " (" + place.Kind + ") at " + place.World.ToString("F0") + ".");
            return "ok";
        }

        private static string SendPoint(string xy)
        {
            string[] parts = (xy ?? "").Split(',');
            if (parts.Length != 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            {
                Core.Log.Warning("[App] send.point: unreadable position '" + xy + "'.");
                return "error:Unreadable position.";
            }
            if (!MapGeo.TryWorld(x, y, out Vector3 world))
            {
                Core.Log.Warning("[App] send.point: the map is not ready.");
                return "error:The map is not ready.";
            }
            Autopilot.SetDestination(world, "Dropped pin");
            Core.Log.Msg("[App] destination sent: dropped pin at " + world.ToString("F0") + " (map " + x.ToString("F3") + ", " + y.ToString("F3") + ").");
            return "ok";
        }

        private static string Clear()
        {
            Autopilot.ClearDestination();
            Core.Log.Msg("[App] destination cleared from the phone.");
            return "ok";
        }

        /// <summary>
        /// Changes quantised, so the pulse does not rebuild the page for a car that only creeps. Includes everything
        /// the state JSON shows.
        /// </summary>
        private static int Revision()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (MapPicture.Ready ? 1 : 0);
                Player local = Player.Local;
                if (local != null)
                {
                    Vector3 p = local.transform.position;
                    h = h * 31 + (int)(p.x / 2f);
                    h = h * 31 + (int)(p.z / 2f);
                    h = h * 31 + (int)(local.transform.eulerAngles.y / 15f);
                }
                LandVehicle car = MyCar();
                if (car != null)
                {
                    Vector3 c = car.transform.position;
                    h = h * 31 + (int)(c.x / 2f);
                    h = h * 31 + (int)(c.z / 2f);
                    h = h * 31 + (int)(Mathf.Abs(car.Speed_Kmh) / 2f);
                }
                h = h * 31 + (int)Autopilot.State;
                h = h * 31 + (Autopilot.Destination.HasValue ? Autopilot.Destination.Value.GetHashCode() : 0);
                h = h * 31 + Autopilot.LastReason.GetHashCode();
                return h;
            }
        }

        private static string Guard(string what, Func<string> body)
        {
            try { return body(); }
            catch (Exception e)
            {
                Core.Log.Error("[App] " + what + " failed: " + e);
                return what == "state" || what == "places" ? "" : "error:" + e.Message;
            }
        }
    }
}
