#if SNITCH
using Snitch.Api;
using Yesla.Driving;

namespace Yesla.Profiling
{
    /// <summary>
    /// DEBUG-only Snitch panel for Yesla. The Snitch host finds this type (leaf name SnitchProbe with a static Register)
    /// on bind. The timings come from the Profiler.Sample scopes at the top of each Yesla hot path ("Yesla.*"); this
    /// panel adds what the follower is doing right now, so a cost spike can be read against the manoeuvre behind it.
    /// Compiled only under the SNITCH symbol - the Release DLL contains zero Snitch types.
    /// </summary>
    internal static class SnitchProbe
    {
        public static void Register()
        {
            Panel p = Profiler.RegisterPanel("Yesla", "Yesla");
            p.Text(() =>
            {
                var car = Autopilot.CurrentCar();
                return car == null ? "no Yesla driven" : "follower: " + PathFollower.LastLineFor(car);
            });
            p.Log();
        }
    }
}
#endif
