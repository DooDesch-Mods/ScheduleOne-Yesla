using Il2CppInterop.Runtime;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI.Input;
using Il2CppScheduleOne.Vehicles;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using Yesla.Config;

namespace Yesla.Driving
{
    /// <summary>
    /// Adds the Yesla keys to the key hints at the bottom left while the local player sits in a Yesla, next to the
    /// game's own Headlights / Handbrake / Exit Vehicle: "First Person" in every seat, "Assisted Driving" for the driver.
    ///
    /// The hint strip only draws descriptors that point at an input action, and the Yesla keys are plain keys from the
    /// preferences, not one of the game's actions. So each entry gets its own action bound to its key; the strip
    /// reads only its binding path to pick the key picture.
    /// </summary>
    internal static class KeyHint
    {
        private const string ModuleId = "yesla_assisted_driving";
        private const string DriveLabel = "Assisted Driving";
        private const string ViewLabel = "First Person";

        private static InputPromptsData _data;
        private static string _dataSig;
        private static string _shownSig;
        private static bool _loaded;   // the strip took the module, so a missing module later was removed by the game

        internal static void Tick()
        {
            LandVehicle car = Autopilot.CurrentCar();
            bool inYesla = car != null && Phone.YeslaApp.IsYesla(car);
            bool driver = inYesla && car.LocalPlayerIsDriver;
            string want = inYesla ? Signature(driver) : null;
            InputPromptsManager prompts = Singleton<InputPromptsManager>.Instance;
            // Vanilla screens (character creator, menus) can clear the hint modules behind our back; put it back.
            if (want != null && want == _shownSig && prompts != null && _loaded && Time.frameCount % 30 == 0 && !prompts.HasActivePrompt(ModuleId))
            {
                Core.Log.Msg("[KeyHint] the game removed the Yesla key hints - showing them again.");
                _shownSig = null;
            }
            if (want == _shownSig) return;
            _shownSig = want;
            _loaded = false;

            if (prompts == null) { Core.Log.Warning("[KeyHint] no InputPromptsManager - the Yesla keys are not shown."); return; }

            if (prompts.HasActivePrompt(ModuleId)) prompts.UnloadModule(ModuleId);
            if (want == null) return;

            InputPromptsData data = Data(driver, want);
            if (data == null) return;
            prompts.LoadModule(data);
            _loaded = prompts.HasActivePrompt(ModuleId);
            if (_loaded) Core.Log.Msg("[KeyHint] shown in the key hints: " + want + ".");
            else Core.Log.Warning("[KeyHint] the key hints refused the Yesla module (" + want + ").");
        }

        internal static void Reset()
        {
            _shownSig = null;
            _loaded = false;
        }

        private static string Signature(bool driver)
        {
            string view = Preferences.ViewKey + " " + ViewLabel;
            return driver ? Preferences.EngageKey + " " + DriveLabel + ", " + view : view;
        }

        private static InputPromptsData Data(bool driver, string sig)
        {
            if (_data != null && _dataSig == sig) return _data;

            // InputActionReference.Create only takes an action that belongs to an asset.
            var asset = ScriptableObject.CreateInstance(Il2CppType.Of<InputActionAsset>()).Cast<InputActionAsset>();
            asset.hideFlags = HideFlags.DontUnloadUnusedAsset;
            InputActionMap map = InputActionSetupExtensions.AddActionMap(asset, "Yesla");

            var data = ScriptableObject.CreateInstance(Il2CppType.Of<InputPromptsData>()).Cast<InputPromptsData>();
            data.hideFlags = HideFlags.DontUnloadUnusedAsset;
            data.Id = ModuleId;
            data.Position = EInputPromptPosition.BottomLeftInGame;
            data.Descriptors = new Il2CppSystem.Collections.Generic.List<InputPromptsDescriptorData>();
            if (driver) Add(data, map, "AssistedDriving", DriveLabel, Preferences.EngageKey);
            Add(data, map, "FirstPerson", ViewLabel, Preferences.ViewKey);
            if (data.Descriptors.Count == 0) return null;

            _data = data;
            _dataSig = sig;
            return data;
        }

        private static void Add(InputPromptsData data, InputActionMap map, string actionName, string label, KeyCode key)
        {
            string path = ControlPath(key);
            if (path == null) { Core.Log.Warning("[KeyHint] no keyboard path for " + key + " - '" + label + "' is not shown."); return; }
            InputAction action = InputActionSetupExtensions.AddAction(map, actionName, InputActionType.Button, path, null, null, null, null);
            var descriptor = ScriptableObject.CreateInstance(Il2CppType.Of<InputPromptsDescriptorData>()).Cast<InputPromptsDescriptorData>();
            descriptor.hideFlags = HideFlags.DontUnloadUnusedAsset;
            descriptor.DisplayName = label;
            descriptor.DisplayColor = Color.white;
            descriptor.Actions = new Il2CppSystem.Collections.Generic.List<InputActionReference>();
            descriptor.Actions.Add(InputActionReference.Create(action));
            data.Descriptors.Add(descriptor);
        }

        /// <summary>
        /// Input System path for a legacy key code; letters, digits and F-keys cover every sensible choice. Input System
        /// paths name the physical key of a US layout, while a legacy letter key follows the keyboard layout, so a
        /// letter is looked up on the current layout: on QWERTZ the key labelled Y is "&lt;Keyboard&gt;/z".
        /// </summary>
        private static string ControlPath(KeyCode key)
        {
            if (key >= KeyCode.A && key <= KeyCode.Z)
            {
                string letter = key.ToString().ToLowerInvariant();
                var keyboard = Keyboard.current;
                var control = keyboard != null ? keyboard.FindKeyOnCurrentKeyboardLayout(letter) : null;
                if (control != null) return "<Keyboard>/" + control.name;
                Core.Log.Warning("[KeyHint] no key on the current keyboard layout types '" + letter + "' - the hint uses the US position.");
                return "<Keyboard>/" + letter;
            }
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9) return "<Keyboard>/" + (key - KeyCode.Alpha0);
            if (key >= KeyCode.F1 && key <= KeyCode.F12) return "<Keyboard>/" + key.ToString().ToLowerInvariant();
            return null;
        }
    }
}
