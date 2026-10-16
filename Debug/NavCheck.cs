#if DEBUG
using System;
using System.Collections;
using Il2CppPathfinding;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Vehicles;
using Il2CppScheduleOne.Vehicles.AI;
using MelonLoader;
using Yesla.Driving;
using Path = Il2CppPathfinding.Path;

namespace Yesla.Debugging
{
    /// <summary>
    /// 'yesla navcheck': walks the steps of the road AI's path calculation one by one for the current car and the
    /// current destination and logs where it stops - road links near start and destination, the snap onto the
    /// general graph, and each of the three path requests with its error text.
    /// </summary>
    internal static class NavCheck
    {
        internal static void Run()
        {
            LandVehicle car = Autopilot.CurrentCar();
            if (car == null || car.Agent == null) { Core.Log.Warning("[NavCheck] not in a car with a road AI."); return; }
            if (Autopilot.Destination == null) { Core.Log.Warning("[NavCheck] no destination - 'yesla go prop <name>' first."); return; }
            MelonCoroutines.Start(Steps(car.Agent, Autopilot.Destination.Value));
        }

        /// <summary>The same steps for any car and target, e.g. a summon the road AI refused.</summary>
        internal static void Run(LandVehicle car, Vector3 dest)
        {
            if (car == null || car.Agent == null) { Core.Log.Warning("[NavCheck] no car with a road AI."); return; }
            MelonCoroutines.Start(Steps(car.Agent, dest));
        }

        private static IEnumerator Steps(VehicleAgent agent, Vector3 dest)
        {
            Vector3 start = agent.frontOfVehiclePosition;
            Core.Log.Msg("[NavCheck] start " + start.ToString("F1") + " -> destination " + dest.ToString("F1") + ".");

            Il2Cpp.AstarPath astar = Il2Cpp.AstarPath.active;
            if (astar == null) { Core.Log.Error("[NavCheck] no active AstarPath - there is no graph at all."); yield break; }
            var graphs = astar.data.graphs;
            for (int i = 0; graphs != null && i < graphs.Length; i++)
            {
                NavGraph g = graphs[i];
                Core.Log.Msg("[NavCheck] graph " + i + ": " + (g == null ? "null" : g.name + " (" + g.GetType().Name + ", " + g.CountNodes() + " nodes)"));
            }

            var startLinks = NodeLink.GetClosestLinks(start, 3);
            var destLinks = NodeLink.GetClosestLinks(dest, 3);
            Core.Log.Msg("[NavCheck] road links near start: " + (startLinks?.Count ?? -1) + ", near destination: " + (destLinks?.Count ?? -1) + ".");
            if (startLinks == null || startLinks.Count == 0 || destLinks == null || destLinks.Count == 0)
            {
                Core.Log.Error("[NavCheck] no road links - the road graph is empty on this peer.");
                yield break;
            }

            Vector3 entry = startLinks[0].GetClosestPoint(start);
            NodeLink exitLink = destLinks[0];
            Vector3 exit = exitLink.GetClosestPoint(dest);
            Vector3 startOnGraph = AstarUtility.GetClosestPointOnGraph(start, "General Vehicle Graph");
            Vector3 destOnGraph = AstarUtility.GetClosestPointOnGraph(dest, "General Vehicle Graph");
            Core.Log.Msg("[NavCheck] entry " + entry.ToString("F1") + ", exit " + exit.ToString("F1") + ", start on graph " + startOnGraph.ToString("F1")
                + ", destination on graph " + destOnGraph.ToString("F1") + ".");

            Path result = null;
            OnPathDelegate done = (Action<Path>)(p => result = p);

            agent.generalSeeker.StartPath(startOnGraph, entry, done);
            for (float t = 0f; result == null && t < 5f; t += Time.deltaTime) yield return null;
            Report("start -> entry (general seeker)", result);

            result = null;
            Vector3 linkStart = NodeLink.GetClosestLinks(entry, 1)[0].Start.position;
            agent.roadSeeker.StartPath(linkStart, exitLink.Start.position, done);
            for (float t = 0f; result == null && t < 5f; t += Time.deltaTime) yield return null;
            Report("entry " + linkStart.ToString("F1") + " -> exit " + exitLink.Start.position.ToString("F1") + " (road seeker)", result);

            result = null;
            agent.generalSeeker.StartPath(exit, destOnGraph, done);
            for (float t = 0f; result == null && t < 5f; t += Time.deltaTime) yield return null;
            Report("exit -> destination (general seeker)", result);
        }

        private static void Report(string leg, Path p)
        {
            if (p == null) { Core.Log.Error("[NavCheck] " + leg + ": no answer within 5 s."); return; }
            if (p.error) Core.Log.Error("[NavCheck] " + leg + ": FAILED - " + p.errorLog);
            else Core.Log.Msg("[NavCheck] " + leg + ": ok, " + (p.vectorPath?.Count ?? 0) + " points, " + p.GetTotalLength().ToString("F0") + " m.");
        }
    }
}
#endif
