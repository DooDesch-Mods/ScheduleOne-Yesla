#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Il2CppFishNet;
using Il2CppScheduleOne;
using Il2CppScheduleOne.Vehicles;
using Il2CppScheduleOne.Vehicles.AI;
using Yesla.Driving;

namespace Yesla.Debugging
{
    /// <summary>
    /// DEBUG-only dev console commands under one word: <c>yesla &lt;subcommand&gt;</c>. Console, not hotkeys, so every
    /// check can be scripted through the bridge. The engage key is a player function and lives in Autopilot.
    ///
    /// Both Console.SubmitCommand overloads are patched (string + List&lt;string&gt;): depending on the caller either
    /// one fires, and dispatch dedupes per frame and signature so a command never runs twice.
    /// </summary>
    internal static class DebugConsolePatch
    {
        internal const string Word = "yesla";

        internal const string Usage =
            "yesla help, yesla state, yesla probe, yesla props, yesla go prop motel, yesla go 0 1 0, " +
            "yesla diag, yesla agentdebug on, yesla engage, yesla stop, yesla clear, yesla spawn hounddog 8, yesla enter, yesla tograph";

        private static readonly List<string> _pending = new List<string>();

        internal static bool TryHandle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return Dispatch(raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        internal static bool TryHandle(Il2CppSystem.Collections.Generic.List<string> args)
        {
            if (args == null || args.Count == 0) return false;
            string[] parts = new string[args.Count];
            for (int i = 0; i < args.Count; i++) parts[i] = args[i];
            return Dispatch(parts);
        }

        private static int _lastFrame = -1;
        private static string _lastSig = "";

        private static bool Dispatch(string[] parts)
        {
            if (parts.Length == 0) return false;
            if (!string.Equals(parts[0], Word, StringComparison.OrdinalIgnoreCase)) return false;

            string sig = string.Join(" ", parts);
            int frame = Time.frameCount;
            if (frame == _lastFrame && sig == _lastSig) return true;
            _lastFrame = frame; _lastSig = sig;

            string sub = parts.Length > 1 ? parts[1].ToLower() : "help";
            try
            {
                switch (sub)
                {
                    case "help":
                        Say("state              - assisted driving state, car, speed, destination");
                        Say("probe              - host/client, seat, road graph distance, agent wiring");
                        Say("props              - every property with its code and position");
                        Say("go prop <name>     - destination = a property (code or part of its name)");
                        Say("go <x> <y> <z>     - destination = a world position");
                        Say("engage / stop      - start or stop assisted driving (same as the key)");
                        Say("steer <x>          - set the steering axis like a key press (0 releases it)");
                        Say("fp [on|off]        - first-person view in a Yesla (same as the view key)");
                        Say("fp eye <x> <y> <z> - eye position in the seat's local space");
                        Say("viz [hud|full|off] - driving visualisation view (same as the visualisation key)");
                        Say("viz save [path]    - write the current visualisation picture to a PNG");
                        Say("vizprobe           - road links, layers, shaders and cameras the visualisation uses");
                        Say("cabin <i> [range] [x y z] - tune the first-person cabin light live");
                        Say("clear              - forget the destination");
                        Say("spawn [code] [m] [yaw] - spawn a player-owned car ahead (default hounddog, 8 m, yaw 0)");
                        Say("enter              - get into the nearest owned car within 10 m");
                        Say("use                - hover and interact with the nearest owned car, like the E key");
                        Say("tp <x> <y> <z>     - move the local player (works on a client too)");
                        Say("tograph            - put the car onto the nearest road (vanilla teleporter)");
                        Say("diag               - road AI internals: target speed, throttle, sensors, path");
                        Say("path [from] [n]    - the planned path points (x,z) with sharp turns marked");
                        Say("agentdebug [off]   - vanilla agent debug logging (obstacles, speed multipliers)");
                        Say("fleet              - Yesla owners, lock, sentry and running summons");
                        Say("summon / honk / lock / sentry - the phone controls, on the nearest own Yesla");
                        Say("claim [code]       - host: give the nearest Yesla to a player code (default: me)");
                        break;

                    case "state":
                        Say(Autopilot.Describe());
                        break;

                    case "probe":
                        Say(Probe());
                        break;

                    case "diag":
                        Say(Diag());
                        break;

                    case "cars":
                    {
                        // Every car this peer knows of; on the host, a client-driven Yesla shows up moving here.
                        var all = NetworkSingleton<VehicleManager>.Instance?.AllVehicles;
                        if (all == null) { Say("cars: no VehicleManager"); break; }
                        int shown = 0;
                        for (int i = 0; i < all.Count; i++)
                        {
                            var v = all[i];
                            if (v == null || !v.IsPlayerOwned) continue;
                            shown++;
                            Say("car " + v.VehicleCode + " at " + v.transform.position.ToString("F1") + " " + v.Speed_Kmh.ToString("F0") + " km/h owner="
                                + v.OwnerId + " occupied=" + v.IsOccupied + " autoDriving=" + (v.Agent != null && v.Agent.AutoDriving));
                        }
                        Say("cars: " + shown + " player-owned of " + all.Count);
                        break;
                    }

                    case "fleet":
                        Say(Vehicle.Fleet.Describe());
                        Say(Summon.Describe());
                        break;

                    case "summon":
                    {
                        var car = Phone.YeslaApp.MyCar();
                        bool coming = car != null && Summon.StatusOf(car).state == SummonState.Coming;
                        string refused = coming ? Summon.Cancel(car) : Summon.Request(car);
                        Say(refused ?? (coming ? "summon: asked to stop" : "summon: asked to come"));
                        break;
                    }

                    case "honk":
                        Say(Net.Remote.Honk(Phone.YeslaApp.MyCar()) ?? "honk: sent");
                        break;

                    case "lock":
                        Say(Net.Remote.ToggleLock(Phone.YeslaApp.MyCar()) ?? "lock: toggle sent");
                        break;

                    case "sentry":
                        Say(Net.Remote.ToggleSentry(Phone.YeslaApp.MyCar()) ?? "sentry: toggle sent");
                        break;

                    case "claim":
                    {
                        string code = parts.Length > 2 ? parts[2] : Vehicle.Fleet.MyCode();
                        Player me = Player.Local;
                        LandVehicle best = null;
                        float bestDist = float.MaxValue;
                        var all = NetworkSingleton<VehicleManager>.Instance?.AllVehicles;
                        if (all == null || me == null) { Say("claim: no VehicleManager or player"); break; }
                        for (int i = 0; i < all.Count; i++)
                        {
                            var v = all[i];
                            if (v == null || !Phone.YeslaApp.IsYesla(v)) continue;
                            float d = Vector3.Distance(v.transform.position, me.transform.position);
                            if (d < bestDist) { bestDist = d; best = v; }
                        }
                        if (best == null) { Say("claim: no Yesla in the world"); break; }
                        Vehicle.Fleet.Claim(best, code, "console claim");
                        Say("claim: " + Vehicle.Fleet.Describe(best));
                        break;
                    }

                    case "dealer":
                        DealerProbe.Run(parts);
                        break;

                    case "dump":
                        PrefabDump.Dump(parts.Length > 2 ? parts[2] : "hounddog");
                        Say("dump written to the log");
                        break;

                    case "unblock":
                    {
                        // A joining test client starts in the character creator and may land in an arrest notice;
                        // both wait for a mouse click the bridge cannot send.
                        var cc = Il2CppScheduleOne.DevUtilities.Singleton<Il2CppScheduleOne.CharacterCreator.CharacterCreator>.Instance;
                        if (cc == null || !cc.IsOpen) Say("unblock: character creator not open");
                        else { cc.Done(); Say("unblock: character creator done with the default looks"); }
                        var notice = Il2CppScheduleOne.DevUtilities.Singleton<Il2CppScheduleOne.UI.ArrestNoticeScreen>.Instance;
                        if (notice == null || !notice.isOpen) Say("unblock: arrest notice not open");
                        else { notice.Exit(); Say("unblock: arrest notice closed"); }
                        break;
                    }

                    case "path":
                    {
                        LandVehicle car = Autopilot.CurrentCar();
                        var path = car?.Agent?.path;
                        if (path == null || path.vectorPath == null) { Say("no path"); break; }
                        int from = 0, count = 40;
                        if (parts.Length > 2) int.TryParse(parts[2], out from);
                        if (parts.Length > 3) int.TryParse(parts[3], out count);
                        var pts = path.vectorPath;
                        var sb = new System.Text.StringBuilder("path " + pts.Count + " pts:");
                        for (int i = Math.Max(0, from); i < Math.Min(pts.Count, from + count); i++)
                        {
                            sb.Append(" ").Append(i).Append("=").Append(pts[i].x.ToString("F0", CultureInfo.InvariantCulture))
                              .Append(",").Append(pts[i].z.ToString("F0", CultureInfo.InvariantCulture));
                            if (i > 0 && i + 1 < pts.Count)
                            {
                                float turn = Vector3.SignedAngle(pts[i] - pts[i - 1], pts[i + 1] - pts[i], Vector3.up);
                                if (Mathf.Abs(turn) > 20f) sb.Append("(").Append(turn.ToString("F0", CultureInfo.InvariantCulture)).Append("°)");
                            }
                        }
                        Say(sb.ToString());
                        break;
                    }

                    case "agentdebug":
                    {
                        LandVehicle car = Autopilot.CurrentCar();
                        if (car == null || car.Agent == null) { Say("not in a car with a road AI"); break; }
                        car.Agent.DEBUG_MODE = parts.Length < 3 || parts[2].ToLower() != "off";
                        Say("vanilla agent debug logging " + (car.Agent.DEBUG_MODE ? "on" : "off"));
                        break;
                    }

                    case "props":
                    {
                        var list = Il2CppScheduleOne.Property.Property.Properties;
                        if (list == null) { Say("no property list yet"); break; }
                        for (int i = 0; i < list.Count; i++)
                        {
                            var p = list[i];
                            if (p == null) continue;
                            Say(p.PropertyCode + " | " + p.PropertyName + " | " + Target(p).ToString("F1") + (p.IsOwned ? " | owned" : ""));
                        }
                        break;
                    }

                    case "go":
                    {
                        if (parts.Length >= 4 && parts[2].ToLower() == "prop")
                        {
                            string query = string.Join(" ", parts, 3, parts.Length - 3);
                            var prop = FindProperty(query);
                            if (prop == null) { Say("no property matches '" + query + "'. Try 'yesla props'."); break; }
                            Autopilot.SetDestination(Target(prop), prop.PropertyName);
                            Say("destination: " + prop.PropertyName + " at " + Target(prop).ToString("F1"));
                            break;
                        }
                        if (parts.Length >= 5
                            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                            && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                            && float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                        {
                            Autopilot.SetDestination(new Vector3(x, y, z), null);
                            Say("destination: " + new Vector3(x, y, z).ToString("F1"));
                            break;
                        }
                        Say("usage: yesla go prop <name>  |  yesla go <x> <y> <z>");
                        break;
                    }

                    case "engage":
                        Say(Autopilot.Engage(out string why) ? "engaged. " + Autopilot.Describe() : "cannot engage: " + why);
                        break;

                    case "steer":
                    {
                        // Writes the steering axis the way a key press does (GameInput sets it only on an input
                        // event, so it holds until 'yesla steer 0' or the next real key).
                        float x = 1f;
                        if (parts.Length > 2) float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out x);
                        GameInput.MotionAxis = new Vector2(x, GameInput.MotionAxis.y);
                        Say("steering axis set to " + x.ToString("F2", CultureInfo.InvariantCulture) + ". " + Autopilot.Describe());
                        break;
                    }

                    case "tp":
                    {
                        // Moves the local player; the vanilla 'teleport' command only runs on the host.
                        if (parts.Length < 5
                            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float tx)
                            || !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float ty)
                            || !float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float tz))
                        { Say("usage: yesla tp <x> <y> <z>"); break; }
                        LandVehicle inCar = Autopilot.CurrentCar();
                        if (inCar != null) { Say("tp: get out of " + inCar.VehicleName + " first"); inCar.ExitVehicle(); }
                        PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerMovement>.Instance.Teleport(new Vector3(tx, ty, tz), false);
                        Say("tp: player moved to " + new Vector3(tx, ty, tz).ToString("F1"));
                        break;
                    }

                    case "walkaway":
                    {
                        // Puts the player down the road ahead of the nearest own Yesla, for a summon test.
                        float metres = 120f;
                        if (parts.Length > 2) float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out metres);
                        LandVehicle car = Phone.YeslaApp.MyCar();
                        if (car == null) { Say("walkaway: no own Yesla"); break; }
                        Vector3 to = car.transform.position + car.transform.forward * metres + Vector3.up * 30f;
                        if (Physics.Raycast(to, Vector3.down, out RaycastHit hit, 80f)) to = hit.point + Vector3.up * 1.2f;
                        else { Say("walkaway: no ground under " + to.ToString("F0")); break; }
                        PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerMovement>.Instance.Teleport(to, false);
                        Say("walkaway: player at " + to.ToString("F1") + ", " + Vector3.Distance(to, car.transform.position).ToString("F0") + " m from the car");
                        break;
                    }

                    case "dropon":
                    {
                        // Spawns a Hounddog 4 m above the nearest own Yesla; the fall is a Sentry hit.
                        LandVehicle car = Phone.YeslaApp.MyCar();
                        if (car == null) { Say("dropon: no own Yesla"); break; }
                        NetworkSingleton<VehicleManager>.Instance.SpawnVehicle("hounddog", car.transform.position + Vector3.up * 4f, car.transform.rotation, true);
                        Say("dropon: a hounddog falls onto " + car.VehicleName + " #" + Vehicle.Fleet.NetId(car));
                        break;
                    }

                    case "navcheck":
                        // 'navcheck x y z' checks the route from the nearest own Yesla to that point.
                        if (parts.Length > 4
                            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float nx)
                            && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float ny)
                            && float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float nz))
                            NavCheck.Run(Phone.YeslaApp.MyCar(), new Vector3(nx, ny, nz));
                        else NavCheck.Run();
                        Say("navcheck runs - see the [NavCheck] lines in the log");
                        break;

                    case "viz":
                    {
                        // 'viz' reports, 'viz hud|full|off' sets the view, 'viz save [path]' writes the current picture.
                        string arg = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
                        if (arg == "hud") Visual.Viz.Set(Visual.VizMode.Hud, "console");
                        else if (arg == "full") Visual.Viz.Set(Visual.VizMode.Full, "console");
                        else if (arg == "off") Visual.Viz.Set(Visual.VizMode.Off, "console");
                        else if (arg == "save")
                        {
                            string path = parts.Length > 3 ? string.Join(" ", parts, 3, parts.Length - 3)
                                : System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "yesla-viz.png");
                            Say(Visual.VizScene.Save(path));
                            break;
                        }
                        else if (arg != "") { Say("usage: yesla viz [hud|full|off|save [path]]"); break; }
                        Say("view " + Visual.Viz.Mode + ", " + Visual.VizScene.Describe());
                        break;
                    }

                    case "cabin":
                    {
                        // 'cabin <intensity> [range] [x y z]' tunes the first-person cabin light live.
                        var f = new List<float>();
                        for (int i = 2; i < parts.Length; i++)
                            if (float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float n)) f.Add(n);
                        if (f.Count == 0) { Say("usage: yesla cabin <intensity> [range] [x y z]"); break; }
                        Vector3? at = f.Count >= 5 ? new Vector3(f[2], f[3], f[4]) : (Vector3?)null;
                        Say(Visual.CabinLight.Tune(f[0], f.Count > 1 ? f[1] : 1.8f, at));
                        break;
                    }

                    case "vizprobe":
                        VizProbe.Run();
                        Say("vizprobe done - see the [VizProbe] lines in the log");
                        break;

                    case "fp":
                    {
                        // 'fp' toggles like the view key, 'fp on|off', 'fp eye <x> <y> <z>' moves the eye in seat space.
                        string arg = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
                        if (arg == "eye" && parts.Length > 5
                            && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float ex)
                            && float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float ey)
                            && float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float ez))
                            FirstPerson.EyeOffset = new Vector3(ex, ey, ez);
                        else if (arg == "on") FirstPerson.Set(true, "console");
                        else if (arg == "off") FirstPerson.Set(false, "console");
                        else if (arg == "") FirstPerson.Set(!FirstPerson.Active, "console");
                        else { Say("usage: yesla fp [on|off] | yesla fp eye <x> <y> <z>"); break; }
                        Say(FirstPerson.Describe());
                        break;
                    }

                    case "stop":
                        Autopilot.Disengage("console stop");
                        Say(Autopilot.Describe());
                        break;

                    case "clear":
                        Autopilot.ClearDestination();
                        Say(Autopilot.Describe());
                        break;

                    case "spawn":
                    {
                        string code = parts.Length > 2 ? parts[2].ToLower() : "hounddog";
                        float dist = 8f;
                        if (parts.Length > 3) float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out dist);
                        Player local = Player.Local;
                        if (local == null) { Say("no local player"); break; }
                        Vector3 fwd = local.transform.forward; fwd.y = 0f; fwd.Normalize();
                        Vector3 pos = local.transform.position + fwd * dist + Vector3.up * 0.5f;
                        float yaw = 0f;
                        if (parts.Length > 4) float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out yaw);
                        Quaternion rot = Quaternion.LookRotation(fwd) * Quaternion.Euler(0f, yaw, 0f);
                        // A Yesla spawned here belongs to whoever spawned it, as if they had bought it.
                        if (code == Vehicle.YeslaVehicleType.Code) Net.YeslaNet.ToHost("req.bought", Vehicle.Fleet.MyCode());
                        NetworkSingleton<VehicleManager>.Instance.SpawnVehicle(code, pos, rot, true);
                        Say("asked the server for a '" + code + "' at " + pos.ToString("F1"));
                        break;
                    }

                    case "tograph":
                    {
                        LandVehicle car = Autopilot.CurrentCar() ?? NearestOwnedCar(10f);
                        if (car == null || car.Agent == null) { Say("no car with a road AI nearby"); break; }
                        car.Agent.Teleporter.MoveToGraph();
                        Say("moved " + car.VehicleName + " onto the road graph: " + car.transform.position.ToString("F1"));
                        break;
                    }

                    case "enter":
                    {
                        // The own Yesla first; a save often owns other cars parked closer.
                        LandVehicle car = Phone.YeslaApp.MyCar();
                        if (car == null || Vector3.Distance(car.transform.position, Player.Local.transform.position) > 10f) car = NearestOwnedCar(10f);
                        if (car == null) { Say("no player-owned car within 10 m"); break; }
                        car.EnterVehicle();
                        Say("entering " + car.VehicleName);
                        break;
                    }

                    case "exit":
                    {
                        LandVehicle car = Autopilot.CurrentCar();
                        if (car == null) { Say("exit: not in a car"); break; }
                        car.ExitVehicle();
                        Say("exit: left " + car.VehicleName);
                        break;
                    }

                    case "use":
                    {
                        // The player's own way in: the hover prompt, then the interact press, as with the E key.
                        LandVehicle car = NearestOwnedCar(10f);
                        if (car == null) { Say("no player-owned car within 10 m"); break; }
                        car.Hovered();
                        Say("use: prompt '" + car.intObj.message + "' state=" + car.intObj.interactionState);
                        car.Interacted();
                        Say("use: interacted with " + car.VehicleName + " - 'yesla probe' shows the seat");
                        break;
                    }

                    default:
                        Say("unknown subcommand '" + sub + "'. Try 'yesla help'.");
                        break;
                }
            }
            catch (Exception e) { Core.Log.Error("[Console] '" + sig + "' failed: " + e); }

            return true;
        }

        private static string Probe()
        {
            string role = InstanceFinder.IsHost ? "host" : (InstanceFinder.IsClient ? "client" : "offline");
            LandVehicle car = Autopilot.CurrentCar();
            if (car == null) return role + " | not in a car";
            var agent = car.Agent;
            string graph = "n/a";
            if (agent != null)
            {
                try { graph = agent.GetDistanceFromVehicleGraph().ToString("F1", CultureInfo.InvariantCulture) + " m"; }
                catch (Exception e) { graph = "threw " + e.GetType().Name + ": " + e.Message; }
            }
            return role + " | car=" + car.VehicleName + " code=" + car.VehicleCode + " driver=" + car.LocalPlayerIsDriver
                 + " simulated=" + car.IsPhysicallySimulated + " kinematic=" + (car.Rb != null && car.Rb.isKinematic)
                 + " agent=" + (agent != null) + " seekers=" + (agent != null && agent.generalSeeker != null && agent.roadSeeker != null)
                 + " graphDistance=" + graph + " | " + Autopilot.Describe();
        }

        /// <summary>The road AI's own view: why it wants the speed it wants, and what its sensors see.</summary>
        private static string Diag()
        {
            LandVehicle car = Autopilot.CurrentCar();
            if (car == null) return "not in a car";
            var a = car.Agent;
            if (a == null) return "car has no road AI";
            var ci = CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.Append("pos=").Append(car.transform.position.ToString("F1"));
            sb.Append(" kmh=").Append(car.Speed_Kmh.ToString("F1", ci));
            sb.Append(" targetSpeed=").Append(a.targetSpeed.ToString("F1", ci));
            sb.Append(" zone=").Append(a.currentSpeedZone != null ? a.currentSpeedZone.speed.ToString("F0", ci) : "none");
            sb.Append(" throttle=").Append(car.throttleOverride.ToString("F2", ci));
            sb.Append(" steer=").Append(car.steerOverride.ToString("F2", ci));
            sb.Append(" handbrake=").Append(car.handbrakeOverride);
            sb.Append(" override=").Append(car.overrideControls);
            sb.Append(" reversing=").Append(a.IsReversing).Append("/").Append(a.reverseCoroutine != null);
            sb.Append(" kinematic=").Append(a.KinematicMode);
            sb.Append(" follower=[").Append(PathFollower.LastLineFor(Autopilot.DrivenCar)).Append("]");
            sb.Append(" targetSteer=").Append(a.targetSteerAngle_Normalized.ToString("F2", ci));
            sb.Append(" turn(min/range/thr/div)=").Append(a.minTurningSpeed.ToString("F0", ci)).Append("/")
              .Append(a.turnSpeedReductionRange.ToString("F0", ci)).Append("/").Append(a.minTurnSpeedReductionAngleThreshold.ToString("F0", ci))
              .Append("/").Append(a.turnSpeedReductionDivisor.ToString("F0", ci)).Append(" turnFlag=").Append(a.Flags.TurnBasedSpeedReduction)
              .Append(" maxSteer=").Append(a.maxSteerAngle.ToString("F0", ci)).Append(" len=").Append(a.vehicleLength.ToString("F1", ci));
            try
            {
                var t = a.speedReductionTracker;
                if (t != null) sb.Append(" reduction(min/avg)=").Append(t.GetLowestValue().ToString("F2", ci)).Append("/").Append(t.GetAverageValue().ToString("F2", ci));
                sb.Append(" stuck=").Append(a.GetIsStuck());
            }
            catch (Exception e) { sb.Append(" reduction=threw " + e.GetType().Name); }
            sb.Append(" obstacleMode=").Append(a.Flags.ObstacleMode).Append(" ignoreLights=").Append(a.Flags.IgnoreTrafficLights);
            var p = a.path;
            if (p != null && p.vectorPath != null && p.vectorPath.Count > 0)
            {
                Vector3 end = p.vectorPath[p.vectorPath.Count - 1];
                sb.Append(" path=").Append(p.vectorPath.Count).Append("pts end=").Append(end.ToString("F0"))
                  .Append(" toEnd=").Append(Vector3.Distance(car.transform.position, end).ToString("F0", ci));
                try
                {
                    Vector3 pos = car.transform.position;
                    Vector3 closest = PathUtility.GetClosestPointOnPath(p, pos, out int si, out int ei, out float lerp);
                    Vector3 ahead = PathUtility.GetAheadPoint(p, pos, 10f);
                    sb.Append(" closest=").Append(closest.ToString("F1")).Append(" #").Append(si).Append(" off=")
                      .Append(Vector3.Distance(pos, closest).ToString("F1", ci))
                      .Append(" ahead10=").Append(ahead.ToString("F1"))
                      .Append(" angleToAhead=").Append(Vector3.SignedAngle(car.transform.forward, ahead - pos, Vector3.up).ToString("F0", ci))
                      .Append(" fwd=").Append(car.transform.forward.ToString("F2"));
                }
                catch (Exception e) { sb.Append(" closest=threw " + e.GetType().Name + ": " + e.Message); }
            }
            else sb.Append(" path=none");
            var sensors = a.sensors;
            if (sensors != null)
            {
                for (int i = 0; i < sensors.Length; i++)
                {
                    var s = sensors[i];
                    if (s == null || s.obstruction == null) continue;
                    sb.Append(" | sensor ").Append(s.name).Append(" -> ").Append(s.obstruction.name)
                      .Append(" (layer ").Append(LayerMask.LayerToName(s.obstruction.gameObject.layer)).Append(") at ")
                      .Append(s.obstructionDistance.ToString("F1", ci)).Append(" m");
                }
            }
            return sb.ToString();
        }

        private static Il2CppScheduleOne.Property.Property FindProperty(string query)
        {
            var list = Il2CppScheduleOne.Property.Property.Properties;
            if (list == null) return null;
            string q = query.ToLower();
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p != null && (p.PropertyCode.ToLower() == q)) return p;
            }
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p != null && p.PropertyName.ToLower().Contains(q)) return p;
            }
            return null;
        }

        /// <summary>Where a car should stop for a property: its map marker, else its spawn point.</summary>
        private static Vector3 Target(Il2CppScheduleOne.Property.Property p)
        {
            if (p.PoI != null) return p.PoI.transform.position;
            if (p.SpawnPoint != null) return p.SpawnPoint.position;
            return p.transform.position;
        }

        private static LandVehicle NearestOwnedCar(float radius)
        {
            Player local = Player.Local;
            // PlayerOwnedVehicles is only filled on the host; IsPlayerOwned is replicated to every peer.
            var all = NetworkSingleton<VehicleManager>.Instance?.AllVehicles;
            if (local == null || all == null) return null;
            LandVehicle best = null;
            float bestDist = radius;
            for (int i = 0; i < all.Count; i++)
            {
                var v = all[i];
                if (v == null || !v.IsPlayerOwned) continue;
                float d = Vector3.Distance(v.transform.position, local.transform.position);
                if (d < bestDist) { best = v; bestDist = d; }
            }
            return best;
        }

        internal static void Say(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            Core.Log.Msg(line);
            _pending.Add("[Yesla] " + line);
        }

        internal static void FlushToConsole()
        {
            if (_pending.Count == 0) return;
            for (int i = 0; i < _pending.Count; i++)
            {
                try { Il2CppScheduleOne.Console.Log(_pending[i]); }
                catch (Exception e) { Core.Log.Warning("[Console] could not echo into the game console: " + e.Message); }
            }
            _pending.Clear();
        }
    }

    [HarmonyPatch(typeof(Il2CppScheduleOne.Console), nameof(Il2CppScheduleOne.Console.SubmitCommand), new Type[] { typeof(string) })]
    internal static class Yesla_Console_SubmitCommand_String_Patch
    {
        private static bool Prefix(string args)
        {
            try { return !DebugConsolePatch.TryHandle(args); }
            catch (Exception e) { Core.Log.Error("[Console] dispatch failed: " + e); return true; }
        }
    }

    [HarmonyPatch(typeof(Il2CppScheduleOne.Console), nameof(Il2CppScheduleOne.Console.SubmitCommand), new Type[] { typeof(Il2CppSystem.Collections.Generic.List<string>) })]
    internal static class Yesla_Console_SubmitCommand_List_Patch
    {
        private static bool Prefix(Il2CppSystem.Collections.Generic.List<string> args)
        {
            try { return !DebugConsolePatch.TryHandle(args); }
            catch (Exception e) { Core.Log.Error("[Console] dispatch failed: " + e); return true; }
        }
    }
}
#endif
