using Il2CppScheduleOne.Map;

namespace Yesla.Phone
{
    /// <summary>
    /// World position to map position and back, normalised to 0..1 across the vanilla map picture.
    ///
    /// The arithmetic is the game's own (<c>MapPositionUtility.GetMapPosition</c>): the offset from the map's origin
    /// point times a factor that puts the edge point at half of <c>MapDimensions</c>. The factor is a private
    /// property in the game, so it is recomputed here from the same two public transforms. Y is flipped because the
    /// map counts upward and a page counts downward.
    /// </summary>
    internal static class MapGeo
    {
        internal static bool TryNormalise(Vector3 world, out double x, out double y)
        {
            x = 0;
            y = 0;
            if (!TryFrame(out MapPositionUtility util, out float size, out _)) return false;

            Vector2 pos = util.GetMapPosition(world);
            x = 0.5 + pos.x / size;
            y = 0.5 - pos.y / size;
            return x >= -0.05 && x <= 1.05 && y >= -0.05 && y <= 1.05;
        }

        /// <summary>
        /// The world point under a normalised map position. Height comes from a ray down onto whatever is there,
        /// because the road AI measures its distance to the graph in 3D.
        /// </summary>
        internal static bool TryWorld(double x, double y, out Vector3 world)
        {
            world = Vector3.zero;
            if (!TryFrame(out MapPositionUtility util, out float size, out float factor)) return false;

            Vector3 origin = util.OriginPoint.position;
            float mx = (float)((x - 0.5) * size);
            float my = (float)((0.5 - y) * size);
            world = new Vector3(origin.x + mx / factor, origin.y, origin.z + my / factor);

            if (Physics.Raycast(new Vector3(world.x, 400f, world.z), Vector3.down, out RaycastHit hit, 800f, ~0, QueryTriggerInteraction.Ignore))
                world.y = hit.point.y;
            else
                Core.Log.Warning("[Map] no ground under " + world.ToString("F0") + " - using the map origin's height.");
            return true;
        }

        /// <summary>The width of the map picture in metres, 0 while the map is not there.</summary>
        internal static double Metres() =>
            TryFrame(out _, out float size, out float factor) ? size / factor : 0;

        private static bool TryFrame(out MapPositionUtility util, out float size, out float factor)
        {
            util = null;
            size = 0f;
            factor = 0f;
            if (!Singleton<MapPositionUtility>.InstanceExists) return false;

            util = Singleton<MapPositionUtility>.Instance;
            size = util.MapDimensions;
            if (size <= 0f || util.OriginPoint == null || util.EdgePoint == null) return false;

            float span = Vector3.Distance(util.OriginPoint.position, util.EdgePoint.position);
            if (span <= 0f) return false;
            factor = size * 0.5f / span;
            return true;
        }
    }
}
