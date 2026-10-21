using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppScheduleOne.Vehicles;
using UnityEngine;
using Yesla.Driving;
using Yesla.Net;

namespace Yesla.Vehicle
{
    /// <summary>
    /// The Yesla's turn signals while Assisted Driving drives: on before a turn on the route, towards the spot while it
    /// parks. The peer that drives decides the side and hands it to Chassis, which passes it on through the host and
    /// flashes the model's <c>Light_IndicatorL</c> and <c>Light_IndicatorR</c> lamps on every player.
    /// </summary>
    internal static class Indicators
    {
        /// <summary>A bend of this many degrees over 15 m is a turn; the road's sweeping curves stay below it.</summary>
        private const float TurnAngle = 40f;
        private const float Sample = 5f;
        /// <summary>The signal goes on this many seconds before the turn, and never later than 25 m or earlier than 50 m.</summary>
        private const float WarnSeconds = 4f, MinWarn = 25f, MaxWarn = 50f;
        /// <summary>The signal stays on this long after the turn is no longer ahead, while the car is still in it.</summary>
        private const float Hold = 1.5f;
        /// <summary>Wheels turned this far the signal's way keep it on until the car is out of the turn.</summary>
        private const float SteeringInTurn = 0.3f;
        /// <summary>A parking spot this far to a side of the car is signalled.</summary>
        private const float SpotAside = 1.5f;
        private const float CheckEvery = 0.2f;
        /// <summary>A signal switches straight from one side to the other only after the new side held this long: an
        /// S-bend into a lot made it flip every check.</summary>
        private const float FlipAfter = 1.2f;

        private static readonly List<Vector3> Pts = new List<Vector3>();
        private static readonly List<Vector3> Samples = new List<Vector3>();

        private static float _nextCheck, _lastSeen;
        private static int _lastSide;
        private static LandVehicle _sentCar;
        private static int _sent;
        private static float _flipSince = -1f;

        /// <summary>Decides the local driver's signal and sends a change.</summary>
        internal static void Tick()
        {
            float now = Time.time;
            if (now >= _nextCheck)
            {
                _nextCheck = now + CheckEvery;
                LandVehicle car = Driving();
                int side = car != null ? Decide(car, now) : 0;
                if (side != 0 && _sent != 0 && side != _sent)
                {
                    if (_flipSince < 0f) _flipSince = now;
                    if (now - _flipSince < FlipAfter) side = _sent;
                }
                else _flipSince = -1f;
                // A drive that ended, or moved to another car, switches the old car's signal off first.
                if (_sentCar != null && _sent != 0 && (car == null || _sentCar.Pointer != car.Pointer))
                {
                    Remote.Blink(_sentCar, 0);
                    _sent = 0;
                }
                if (car != null && side != _sent)
                {
                    Remote.Blink(car, side);
                    _sent = side;
                }
                _sentCar = car;
            }
        }

        /// <summary>The car this player's Assisted Driving moves right now, or null.</summary>
        private static LandVehicle Driving()
        {
            LandVehicle car = Autopilot.DrivenCar;
            return car != null && car.gameObject != null ? car : null;
        }

        /// <summary>-1 left, 1 right, 0 off.</summary>
        private static int Decide(LandVehicle car, float now)
        {
            if (Autopilot.State == DriveState.Parking)
            {
                _lastSide = 0;
                ParkTarget spot = Parking.Current;
                if (spot == null || Parking.Settling) return 0;
                Vector3 right = Vector3.Cross(Vector3.up, Flat(car.transform.forward).normalized);
                float aside = Vector3.Dot(Flat(spot.Centre - car.transform.position), right);
                return Mathf.Abs(aside) > SpotAside ? (aside > 0f ? 1 : -1) : 0;
            }
            int ahead = TurnAhead(car);
            if (ahead != 0)
            {
                _lastSide = ahead;
                _lastSeen = now;
                return ahead;
            }
            if (_lastSide != 0)
            {
                // Still in the turn: the wheels point the signal's way.
                float steer = car.steerOverride;
                if (steer * _lastSide > SteeringInTurn) _lastSeen = now;
                if (now - _lastSeen < Hold) return _lastSide;
                _lastSide = 0;
            }
            return 0;
        }

        /// <summary>The side of the first turn on the road AI's path within the warning distance, or 0.</summary>
        private static int TurnAhead(LandVehicle car)
        {
            var agent = car.Agent;
            var vp = agent != null && agent.AutoDriving && agent.path != null ? agent.path.vectorPath : null;
            if (vp == null || vp.Count < 2) return 0;
            Vector3 pos = car.transform.position;
            int seg = 0;
            float best = float.MaxValue, t = 0f;
            for (int i = 0; i + 1 < vp.Count; i++)
            {
                Vector3 a = Flat(vp[i]), ab = Flat(vp[i + 1] - vp[i]);
                float l = ab.sqrMagnitude;
                float k = l < 0.0001f ? 0f : Mathf.Clamp01(Vector3.Dot(Flat(pos) - a, ab) / l);
                float d = (a + ab * k - Flat(pos)).sqrMagnitude;
                if (d < best) { best = d; seg = i; t = k; }
            }
            float warn = Mathf.Clamp(Mathf.Abs(car.Speed_Kmh) / 3.6f * WarnSeconds, MinWarn, MaxWarn);
            Samples.Clear();
            Samples.Add(Vector3.Lerp(vp[seg], vp[seg + 1], t));
            float carry = 0f;
            Vector3 from = Samples[0];
            for (int i = seg + 1; i < vp.Count && Samples.Count * Sample < warn + 3f * Sample; i++)
            {
                Vector3 to = vp[i];
                float len = Flat(to - from).magnitude;
                float step = Sample - carry;
                while (step <= len) { Samples.Add(Vector3.Lerp(from, to, step / len)); step += Sample; }
                carry = len - (step - Sample);
                from = to;
            }
            for (int k = 1; k + 2 < Samples.Count && k * Sample <= warn; k++)
            {
                float angle = Vector3.SignedAngle(Flat(Samples[k] - Samples[k - 1]), Flat(Samples[k + 2] - Samples[k + 1]), Vector3.up);
                if (Mathf.Abs(angle) >= TurnAngle) return angle > 0f ? 1 : -1;
            }
            return 0;
        }

        private static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        internal static void Reset()
        {
            _sentCar = null;
            _sent = 0;
            _lastSide = 0;
            _flipSince = -1f;
        }
    }
}
