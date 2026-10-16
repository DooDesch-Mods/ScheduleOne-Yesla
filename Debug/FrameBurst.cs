#if DEBUG
using System.IO;
using UnityEngine;

namespace Yesla.Debugging
{
    /// <summary>
    /// 'yesla frames [count]': saves that many consecutive frames as PNGs, one per rendered frame. A screenshot from
    /// outside lands on a random frame, so a flicker that lasts one frame is caught only by luck; this catches it
    /// every time. The files go to UserData/YeslaFrames, the folder is emptied on each start.
    /// </summary>
    internal static class FrameBurst
    {
        private const int MaxFrames = 120;

        private static int _left, _index;
        private static string _dir;

        internal static string Start(int count)
        {
            if (count < 1 || count > MaxFrames) return "frames: count must be 1-" + MaxFrames + ".";
            _dir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "YeslaFrames");
            Directory.CreateDirectory(_dir);
            foreach (string f in Directory.GetFiles(_dir, "*.png")) File.Delete(f);
            _left = count;
            _index = 0;
            return "frames: capturing " + count + " consecutive frames to " + _dir;
        }

        internal static void LateTick()
        {
            if (_left <= 0) return;
            ScreenCapture.CaptureScreenshot(Path.Combine(_dir, "f" + _index.ToString("D3") + "_" + Time.frameCount + ".png"));
            _index++;
            if (--_left == 0) Core.Log.Msg("[Frames] " + _index + " frame(s) queued for " + _dir + ".");
        }
    }
}
#endif
