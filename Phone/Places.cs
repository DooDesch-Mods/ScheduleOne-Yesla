using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Quests;
using Il2CppScheduleOne.Vehicles;

namespace Yesla.Phone
{
    /// <summary>A destination the app can list: one visible map marker.</summary>
    internal sealed class Place
    {
        public string Id;
        public string Label;
        public string Kind;
        public Vector3 World;
        public double X;
        public double Y;
        public float Distance;
    }

    /// <summary>
    /// The places the Yesla app offers as destinations: every marker the vanilla map shows right now - owned
    /// properties and businesses, customers, dealers, suppliers, dead drops - plus the active quest steps.
    ///
    /// The map's own markers are the list because they are exactly what the player already knows as "places", and
    /// the game hides the ones that do not apply (an unmet customer, an unowned property) by disabling the POI
    /// component. Quest steps and owned cars carry a POI too; they are told apart by the object the POI sits under.
    /// </summary>
    internal static class Places
    {
        /// <summary>Nearest first, cut here so the page never builds hundreds of rows in one script turn.</summary>
        private const int MaxPlaces = 120;

        private static List<Place> _last = new List<Place>();

        internal static List<Place> Last => _last;

        internal static List<Place> Collect()
        {
            var list = new List<Place>();
            var all = UnityEngine.Object.FindObjectsOfType<POI>();
            if (all == null) { _last = list; return list; }

            Player local = Player.Local;
            Vector3 from = local != null ? local.transform.position : Vector3.zero;

            for (int i = 0; i < all.Length; i++)
            {
                POI poi = all[i];
                // isActiveAndEnabled: a disabled POI component is how the game hides a marker.
                if (poi == null || !poi.isActiveAndEnabled) continue;
                if (local != null && local.PoI != null && local.PoI.GetInstanceID() == poi.GetInstanceID()) continue;

                Vector3 world = poi.transform.position;
                if (!MapGeo.TryNormalise(world, out double x, out double y)) continue;

                NPC npc = poi.TryCast<NPCPoI>()?.NPC;
                string label = Clean(poi.MainText);
                string kind = KindOf(poi, npc, ref label);
                if (kind == null || string.IsNullOrEmpty(label)) continue;

                list.Add(new Place
                {
                    Id = npc != null && !string.IsNullOrEmpty(npc.ID) ? "npc-" + npc.ID : "poi-" + poi.GetInstanceID(),
                    Label = label,
                    Kind = kind,
                    World = world,
                    X = x,
                    Y = y,
                    Distance = Vector3.Distance(from, world),
                });
            }

            // Quest steps first, then by distance: the current job is the destination asked for most.
            list.Sort((a, b) =>
            {
                int qa = a.Kind == "quest" ? 0 : 1, qb = b.Kind == "quest" ? 0 : 1;
                return qa != qb ? qa.CompareTo(qb) : a.Distance.CompareTo(b.Distance);
            });
            if (list.Count > MaxPlaces) list.RemoveRange(MaxPlaces, list.Count - MaxPlaces);

            _last = list;
            return list;
        }

        internal static Place Find(string id)
        {
            for (int i = 0; i < _last.Count; i++) if (_last[i].Id == id) return _last[i];
            Collect();
            for (int i = 0; i < _last.Count; i++) if (_last[i].Id == id) return _last[i];
            return null;
        }

        /// <summary>
        /// Asked of the owner: a POI carries no type of its own beyond NPCPoI. A quest step's POI is a child of its
        /// QuestEntry (QuestEntry.CreatePoI), and an owned car's is a child of the car. Null drops the place: the
        /// Yesla itself is no destination for the Yesla.
        /// </summary>
        private static string KindOf(POI poi, NPC npc, ref string label)
        {
            if (npc != null)
            {
                if (npc.GetComponent<Dealer>() != null) return "dealer";
                if (npc.GetComponent<Supplier>() != null) return "supplier";
                return "customer";
            }
            QuestEntry entry = poi.GetComponentInParent<QuestEntry>();
            if (entry != null)
            {
                string title = Clean(entry.Title);
                if (!string.IsNullOrEmpty(title)) label = title;
                return "quest";
            }
            LandVehicle car = poi.GetComponentInParent<LandVehicle>();
            if (car != null)
            {
                if (YeslaApp.IsYesla(car)) return null;
                // "Owned Vehicle (Yellow Cheetah)" lists as "Yellow Cheetah".
                int open = label.IndexOf('('), close = label.LastIndexOf(')');
                if (open >= 0 && close > open) label = label.Substring(open + 1, close - open - 1).Trim();
                return "vehicle";
            }
            if (poi.GetComponentInParent<SupplierStash>() != null) return "stash";
            if (poi.GetComponentInParent<Il2CppScheduleOne.Property.Property>() != null) return "property";
            return "place";
        }

        /// <summary>Strips rich-text tags and line breaks the map labels sometimes carry.</summary>
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new System.Text.StringBuilder(s.Length);
            bool inTag = false;
            foreach (char c in s)
            {
                if (c == '<') { inTag = true; continue; }
                if (c == '>') { inTag = false; continue; }
                if (inTag) continue;
                sb.Append(c == '\n' || c == '\r' ? ' ' : c);
            }
            return sb.ToString().Trim();
        }
    }
}
