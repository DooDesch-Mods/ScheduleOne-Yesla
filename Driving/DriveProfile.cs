using System;

namespace Yesla.Driving
{
    /// <summary>
    /// How hard Assisted Driving and Summon drive: speed, how fast a bend is taken, how hard the car brakes, and how
    /// long it waits behind something. The player picks one on the phone; it is stored per player.
    /// </summary>
    internal sealed class DriveProfile
    {
        internal string Id;
        internal string Label;

        /// <summary>Speed limit multiplier; ignored when <see cref="NoLimit"/> is set.</summary>
        internal float LimitFactor = 1f;
        /// <summary>No speed limit at all: as fast as the car, the bends and the traffic ahead allow.</summary>
        internal bool NoLimit;
        /// <summary>Highest speed in km/h the planner asks for, for a car with no limit.</summary>
        internal float MaxKmh = 200f;

        /// <summary>Sideways acceleration in m/s² a bend may need: v = sqrt(a * r).</summary>
        internal float LateralAccel;
        /// <summary>Deceleration in m/s² the planner brakes with ahead of a bend, an obstacle or the destination.</summary>
        internal float BrakeDecel;
        /// <summary>Metres kept to a stopped car or person ahead.</summary>
        internal float StopGap;

        internal bool IgnoreTrafficLights;

        /// <summary>Waiting this long behind a parked car or a bin starts a bypass.</summary>
        internal float WaitBeforeBypass;
        /// <summary>A car in traffic is waited for this long before the car passes it.</summary>
        internal float WaitBehindTraffic;

        internal static readonly DriveProfile Chill = new DriveProfile
        {
            Id = "chill", Label = "Chill",
            LateralAccel = 2.0f, BrakeDecel = 2.5f, StopGap = 6f,
            WaitBeforeBypass = 4f, WaitBehindTraffic = 25f,
        };

        internal static readonly DriveProfile Standard = new DriveProfile
        {
            Id = "standard", Label = "Standard",
            LateralAccel = 3.0f, BrakeDecel = 3.5f, StopGap = 5f,
            WaitBeforeBypass = 4f, WaitBehindTraffic = 25f,
        };

        internal static readonly DriveProfile MadMax = new DriveProfile
        {
            Id = "madmax", Label = "Mad Max",
            NoLimit = true, MaxKmh = 200f,
            LateralAccel = 4.2f, BrakeDecel = 4.0f, StopGap = 4f,
            IgnoreTrafficLights = true,
            WaitBeforeBypass = 1.5f, WaitBehindTraffic = 5f,
        };

        internal static readonly DriveProfile[] All = { Chill, Standard, MadMax };

        /// <summary>The profile with this id, or null.</summary>
        internal static DriveProfile Find(string id)
        {
            foreach (DriveProfile p in All)
                if (string.Equals(p.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        /// <summary>The local player's profile, from the preferences.</summary>
        internal static DriveProfile Current => Find(Config.Preferences.DriveProfileId) ?? Standard;

        /// <summary>Stores <paramref name="id"/> as the local player's profile. Returns the profile, or null when the
        /// id is unknown.</summary>
        internal static DriveProfile Select(string id)
        {
            DriveProfile p = Find(id);
            if (p == null) { Core.Log.Warning("[Profile] unknown drive profile '" + id + "' - keeping " + Current.Label + "."); return null; }
            Config.Preferences.DriveProfileId = p.Id;
            Core.Log.Msg("[Profile] drive profile set to " + p.Label + ".");
            // A drive in progress switches at once; the route it is on stays.
            if (Autopilot.DrivenCar != null) PathFollower.ChangeProfile(Autopilot.DrivenCar, p);
            return p;
        }
    }
}
