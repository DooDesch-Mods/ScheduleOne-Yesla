using System;
using System.Collections.Generic;
using Chassis.Api;
using Il2CppScheduleOne.Vehicles;

namespace Yesla.Vehicle
{
    /// <summary>
    /// The Yesla's Chassis profile: the seated driver with hands on the turning steering wheel, the first-person eyes,
    /// the frunk and boot lids, the wipers, the indicator lamps, the Yesla horn and chirp, and the automatic headlights.
    /// Chassis moves all of them on every peer; Yesla only describes where they are on its model.
    ///
    /// The pivots and their axes come from <see cref="YeslaBody"/>, which hangs each moving part from a pivot at its
    /// hinge and fills <see cref="MovingParts.Axes"/>; the seats are fitted from the donor's vanilla seats.
    /// </summary>
    internal static class YeslaChassis
    {
        internal const string Frame = "YeslaModel";
        internal const string HornSound = "yesla.horn", ChirpSound = "yesla.chirp";

        /// <summary>
        /// Hips against the donor's seat transforms, which sit at the Hounddog's origin height, near the steering wheel.
        /// The Yesla is low for the game's large heads: this low, the hair stays under the roof.
        /// </summary>
        private const float HipHeight = -0.59f;

        /// <summary>The first-person eyes sit this much above the body's own, so the view clears the steering wheel; the own head is hidden there.</summary>
        private const float EyeLift = 0.115f;

        /// <summary>Cabin floor in model space; the feet rest on it instead of the fitted point under it.</summary>
        private const float FloorY = 0.15f;

        /// <summary>Registers the profile from the built prefab; every peer does it once per scene.</summary>
        internal static void Register(LandVehicle prefab)
        {
            if (prefab == null) { Core.Log.Error("[Chassis] no Yesla prefab - the car keeps vanilla seats and no moving parts."); return; }
            try
            {
                Sounds();
                Transform frame = Visual.VizScene.FindDeep(prefab.transform, Frame);
                if (frame == null) { Core.Log.Error("[Chassis] the prefab has no " + Frame + " - the Chassis profile is not registered."); return; }
                Transform steering = frame.Find(MovingParts.PivotPrefix + "Steering");
                if (steering == null) Core.Log.Warning("[Chassis] no steering wheel pivot on the model - the driver's hands rest on the lap.");

                var profile = new ChassisProfile
                {
                    Code = YeslaVehicleType.Code,
                    Frame = Frame,
                    Steering = steering != null && Axis("Steering", out Vector3 column)
                        ? new SteeringProfile { Path = steering.name, Axis = column, Ratio = 5f, Max = 170f }
                        : null,
                    Lamps = new LampProfile
                    {
                        Left = new List<string> { "Light_IndicatorL" },
                        Right = new List<string> { "Light_IndicatorR" },
                        UseTexture = true,
                    },
                    Horn = new HornProfile { Clip = HornSound, Chirp = ChirpSound, Position = new Vector3(0f, 0.6f, 1.8f), Volume = 0.8f },
                    AutoLights = true,
                };
                AddPart(profile, "frunk", "lid", "Bonnet", 55f, 1.2f, 1.2f, "front");
                AddPart(profile, "trunk", "lid", "Boot", 70f, 1.2f, 1.2f, "rear");
                AddPart(profile, "wiper_l", "wiper", "Wiper_L", 72f, 1.6f, 0.8f, "");
                AddPart(profile, "wiper_r", "wiper", "Wiper_R", 72f, 1.6f, 0.8f, "");

                var seats = prefab.Seats;
                for (int i = 0; seats != null && i < seats.Length; i++)
                {
                    if (seats[i] == null) continue;
                    bool driver = seats[i].isDriverSeat;
                    SeatProfile seat = ChassisApi.FitSeat(prefab, i, frame, driver ? steering : null, HipHeight);
                    seat.Eyes += Vector3.up * EyeLift;
                    Vector3 feet = seat.Feet.Position;
                    feet.y = Mathf.Max(feet.y, FloorY);
                    seat.Feet.Position = feet;
                    profile.Seats.Add(seat);
                }

                bool used = ChassisApi.Register(profile);
                Core.Log.Msg("[Chassis] Yesla profile " + (used ? "registered" : "overridden by a JSON file") + ": " + profile.Seats.Count + " seat(s), "
                             + profile.Parts.Count + " moving part(s), steering " + (profile.Steering != null ? "on" : "off") + ".");
            }
            catch (Exception e) { Core.Log.Error("[Chassis] registering the Yesla profile failed - the car keeps vanilla seats and no moving parts: " + e); }
        }

        private static void AddPart(ChassisProfile profile, string name, string kind, string pivot, float angle, float seconds, float fast, string storage)
        {
            if (!Axis(pivot, out Vector3 axis)) { Core.Log.Warning("[Chassis] no axis for " + pivot + " - the " + name + " stays still."); return; }
            profile.Parts.Add(new PartProfile
            {
                Name = name, Kind = kind, Path = MovingParts.PivotPrefix + pivot, Axis = axis,
                Angle = angle, Seconds = seconds, FastSeconds = fast, Storage = storage,
            });
        }

        /// <summary>The model hinges sit at their pivot with no rotation, so the model-space axis is the pivot's own.</summary>
        private static bool Axis(string part, out Vector3 axis) => MovingParts.Axes.TryGetValue(part, out axis);

        private static void Sounds()
        {
            AudioClip horn = Clips.Get("horn"), chirp = Clips.Get("chirp");
            if (horn != null) ChassisApi.RegisterSound(HornSound, horn);
            else Core.Log.Warning("[Chassis] no Yesla horn recording - Chassis plays its built-in horn.");
            if (chirp != null) ChassisApi.RegisterSound(ChirpSound, chirp);
            else Core.Log.Warning("[Chassis] no Yesla chirp recording - Chassis plays its built-in chirp.");
        }
    }
}
