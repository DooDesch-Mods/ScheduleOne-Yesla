using System;
using System.Globalization;
using Il2CppScheduleOne.Vehicles;
using Il2CppScheduleOne.Vehicles.Modification;
using Sideload.Api;
using Yesla.Config;
using Yesla.Driving;
using Yesla.Net;
using Yesla.Vehicle;

namespace Yesla.Phone
{
    /// <summary>
    /// The Yesla phone app: a map with the player, the car and the destination, a list of places, "Send to car",
    /// and the remote controls (Summon, Honk, Lock, Sentry). Rendered by Sideload from <c>Assets/yesla/</c>;
    /// without Sideload every call here is a no-op and the car still drives through the console and the engage key.
    ///
    /// The destination is the local player's: whoever sends it is the one who drives, on their own machine, so a
    /// co-op client needs no network call to use it. The remote controls go through the host, which checks that
    /// the asking player owns the car.
    /// </summary>
    internal static class YeslaApp
    {
        internal const string AppId = "yesla";

        private const float PulseSeconds = 0.25f;
        private const float PlacesSeconds = 5f;
        private const float IconSeconds = 2f;

        private static AppHandle _app;
        private static float _nextPulse;
        private static float _nextPlaces;
        private static float _nextIcon;
        private static bool? _iconShown;
        private static int _pushedState = int.MinValue;
        private static int _pushedPlaces = int.MinValue;

        internal static void Register()
        {
            _app = Apps.Register(AppId, "Yesla.Assets.yesla", "Yesla", "Yesla");
            _app.OnCall("yesla.state", _ => Guard("state", State))
                .OnCall("yesla.places", _ => Guard("places", PlacesJson))
                .OnCall("yesla.send.place", id => Guard("send.place", () => SendPlace(id)))
                .OnCall("yesla.send.point", xy => Guard("send.point", () => SendPoint(xy)))
                .OnCall("yesla.clear", _ => Guard("clear", Clear))
                .OnCall("yesla.profile", id => Guard("profile", () => DriveProfile.Select(id) != null ? "ok" : "error:Unknown drive profile."))
                .OnCall("yesla.parking", mode => Guard("parking", () => SetParking(mode)))
                .OnCall("yesla.summon", _ => Guard("summon", ToggleSummon))
                .OnCall("yesla.honk", _ => Guard("honk", () => Answer(Remote.Honk(MyCar()))))
                .OnCall("yesla.lock", _ => Guard("lock", () => Answer(Remote.ToggleLock(MyCar()))))
                .OnCall("yesla.sentry", _ => Guard("sentry", () => Answer(Remote.ToggleSentry(MyCar()))))
                .OnCall("yesla.palette", _ => Guard("palette", PaletteJson))
                .OnCall("yesla.paint", id => Guard("paint", () => Paint(id)))
                .OnCall("yesla.plate", text => Guard("plate", () => Answer(Remote.SetPlate(MyCar(), text))));
            Summon.MineChanged += OnSummonChanged;
            Sentry.Alerted += OnSentryAlert;
            Core.Log.Msg(Apps.Available
                ? "[App] registered with Sideload."
                : "[App] Sideload not loaded yet - the Yesla app registration is queued.");
        }

        internal static void Tick()
        {
#if SNITCH
            using var __perf = Snitch.Api.Profiler.Sample("Yesla.YeslaApp.Tick");
#endif
            if (_app == null) return;
            if (_app.IsOnScreen) CarPhoto.Step(_app);
            float now = Time.unscaledTime;
            if (now < _nextPulse) return;
            _nextPulse = now + PulseSeconds;

            MapPicture.Ensure(_app);
            UpdateIcon(now);
            if (!_app.IsOnScreen) return;

            LandVehicle mine = MyCar();
            MapRoute.Update(mine);
            Fleet.Rec mineRec = mine != null ? Fleet.For(mine) : null;
            CarPhoto.Update(_app, mine, mineRec != null ? mineRec.PlateText : "", force: false);
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

        /// <summary>The app is on the home screen only while the local player owns a Yesla.</summary>
        private static void UpdateIcon(float now)
        {
            if (now < _nextIcon) return;
            _nextIcon = now + IconSeconds;
            bool owns = Fleet.MyCars().Count > 0;
            // Stated again every few seconds: a phone rebuild puts the icon back to its default.
            _app.Icon(owns);
            if (owns == _iconShown) return;
            _iconShown = owns;
            Core.Log.Msg(owns
                ? "[App] the local player owns a Yesla - the app is on the phone."
                : "[App] the local player owns no Yesla - the app is hidden.");
        }

        private static void OnSummonChanged(LandVehicle car, SummonState state, string message)
        {
            if (_app == null) return;
            _app.Emit("yesla.changed", "");
            if (state == SummonState.Arrived) _app.Notify("Yesla", message.Length > 0 ? message : "Your car is here.", 4f);
            else if (state == SummonState.Failed) _app.Notify("Yesla", message.Length > 0 ? message : "Summon stopped.", 4f);
        }

        /// <summary>A phone notification from the Yesla app.</summary>
        internal static void Notify(string title, string text, float seconds)
        {
            if (_app == null) { Core.Log.Warning("[App] no app registered - notification '" + title + ": " + text + "' not shown."); return; }
            _app.Notify(title, text, seconds);
            Core.Log.Msg("[App] notification: " + title + ": " + text);
        }

        private static void OnSentryAlert(LandVehicle car, string kind)
        {
            if (_app == null) return;
            _app.Notify("Sentry Mode", kind == "hit" ? "Something hit your Yesla." : "Someone is at your Yesla.", 5f);
            _app.Emit("yesla.changed", "");
        }

        internal static void Reset()
        {
            MapPicture.Reset();
            CarPicture.Reset();
            CarPhoto.Reset();
            MapRoute.Clear("the session ended");
            _pushedState = int.MinValue;
            _pushedPlaces = int.MinValue;
            _iconShown = null;
            _nextIcon = 0f;
        }

        /// <summary>The Yesla this player is sitting in, the one Assisted Driving steers, or the nearest one they own.</summary>
        internal static LandVehicle MyCar()
        {
            LandVehicle sitting = Autopilot.CurrentCar();
            if (sitting != null && IsYesla(sitting)) return sitting;
            if (Autopilot.DrivenCar != null) return Autopilot.DrivenCar;

            Player local = Player.Local;
            if (local == null) return null;
            LandVehicle best = null;
            float bestDist = float.MaxValue;
            foreach (var v in Fleet.MyCars())
            {
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
                Fleet.Rec rec = Fleet.For(car);
                EVehicleColor paint = car.OwnedColor;
                j.Key("car").Open()
                    .Prop("x", cx).Prop("y", cy).Prop("h", car.transform.eulerAngles.y)
                    .Prop("name", car.VehicleName)
                    .Prop("speed", Mathf.Abs(car.Speed_Kmh) * Visual.VizOverlay.ShownSpeed)
                    .Prop("inside", Autopilot.CurrentCar() != null && Autopilot.CurrentCar().Pointer == car.Pointer)
                    .Prop("distance", local != null ? Vector3.Distance(local.transform.position, car.transform.position) : 0)
                    .Prop("mine", Fleet.IsMine(car))
                    .Prop("locked", rec != null && rec.Locked)
                    .Prop("sentry", rec != null && rec.Sentry)
                    .Prop("paint", paint.ToString())
                    .Prop("picture", CarPhoto.Current ?? CarPicture.Ensure(_app, paint) ?? "")
                    .Prop("view", CarPhoto.ViewOf(car));
                if (CarPhoto.ViewNames != null) j.Key("views").OpenArray().Strings(CarPhoto.ViewNames).CloseArray();
                j
                    .Prop("plate", rec != null ? rec.PlateText : "")
                    .Prop("plateOwn", rec != null ? rec.Plate : "")
                    .Prop("plateDefault", rec != null ? rec.DefaultPlate : "")
                    .Prop("top", car.TopSpeed)
                    .Close();
                var summon = Summon.StatusOf(car);
                j.Key("summon").Open().Prop("state", summon.state.ToString()).Prop("reason", summon.reason).Close();
            }
            else { j.Null("car"); j.Null("summon"); }

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

            MapRoute.Write(j);

            j.Key("drive").Open()
                .Prop("state", Autopilot.State.ToString())
                .Prop("reason", Autopilot.LastReason)
                .Prop("key", Preferences.EngageKey.ToString())
                .Prop("profile", DriveProfile.Current.Id)
                .Prop("parking", Preferences.ParkDirection)
                .Close();

            return j.Close().ToString();
        }

        /// <summary>Autopark direction from the phone: "auto", "reverse" or "forward".</summary>
        private static string SetParking(string mode)
        {
            string m = (mode ?? string.Empty).Trim().ToLowerInvariant();
            if (m != "auto" && m != "reverse" && m != "forward") { Core.Log.Warning("[App] unknown parking direction '" + mode + "'."); return "error:Unknown parking direction."; }
            Preferences.ParkDirection = m;
            Core.Log.Msg("[App] autopark set to " + (m == "auto" ? "pick per spot" : m == "reverse" ? "backwards" : "nose first") + ".");
            return "ok";
        }

        /// <summary>The game's paints, in its own order; Custom is the editor's and has no colour of its own.</summary>
        private static string PaletteJson()
        {
            var j = new Json().OpenArray();
            if (Singleton<VehicleColors>.InstanceExists)
            {
                var lib = Singleton<VehicleColors>.Instance.colorLibrary;
                for (int i = 0; i < lib.Count; i++)
                {
                    var c = lib[i];
                    if (c == null || c.color == EVehicleColor.Custom) continue;
                    Color32 ui = c.UIColor;
                    j.Open().Prop("id", c.color.ToString()).Prop("name", c.colorName)
                     .Prop("hex", "#" + ui.r.ToString("x2") + ui.g.ToString("x2") + ui.b.ToString("x2")).Close();
                }
            }
            else Core.Log.Warning("[App] no VehicleColors - the paint tab is empty.");
            return j.CloseArray().ToString();
        }

        /// <summary>Repaint through the game's own path: saved with the car and sent to every player.</summary>
        private static string Paint(string id)
        {
            LandVehicle car = MyCar();
            if (car == null) return "error:No Yesla.";
            if (!Fleet.IsMine(car)) return "error:This Yesla is not yours.";
            if (!Enum.TryParse(id, out EVehicleColor paint) || paint == EVehicleColor.Custom)
            {
                Core.Log.Warning("[App] paint: unknown paint '" + id + "'.");
                return "error:Unknown paint.";
            }
            car.SendOwnedColor(paint);
            Core.Log.Msg("[App] " + car.VehicleName + " #" + Fleet.NetId(car) + " repainted " + paint + ".");
            return "ok";
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

        /// <summary>Summon while the car is not coming, stop it while it is.</summary>
        private static string ToggleSummon()
        {
            LandVehicle car = MyCar();
            bool coming = car != null && Summon.StatusOf(car).state == SummonState.Coming;
            return Answer(coming ? Summon.Cancel(car) : Summon.Request(car));
        }

        /// <summary>A refusal reason becomes the page's "error:" answer, null becomes "ok".</summary>
        private static string Answer(string refused)
        {
            if (refused == null) return "ok";
            Core.Log.Msg("[App] refused on the phone: " + refused);
            return "error:" + refused;
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
                    h = h * 31 + (int)car.OwnedColor;
                }
                h = h * 31 + (int)Autopilot.State;
                h = h * 31 + (Autopilot.Destination.HasValue ? Autopilot.Destination.Value.GetHashCode() : 0);
                h = h * 31 + Autopilot.LastReason.GetHashCode();
                h = h * 31 + Fleet.Revision;
                h = h * 31 + Summon.Revision;
                h = h * 31 + DriveProfile.Current.Id.GetHashCode();
                h = h * 31 + MapRoute.Revision;
                h = h * 31 + (CarPhoto.Current ?? string.Empty).GetHashCode();
                h = h * 31 + CarPhoto.Revision;
                h = h * 31 + (car != null ? CarPhoto.ViewOf(car) : -1);
                h = h * 31 + MapRoute.At;
                return h;
            }
        }

        private static string Guard(string what, Func<string> body)
        {
            try { return body(); }
            catch (Exception e)
            {
                Core.Log.Error("[App] " + what + " failed: " + e);
                return what == "state" || what == "places" || what == "palette" ? "" : "error:" + e.Message;
            }
        }
    }
}
