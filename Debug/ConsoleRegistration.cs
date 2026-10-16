#if DEBUG
using System;
using Il2CppInterop.Runtime.Injection;
using MelonLoader;
using GameConsole = Il2CppScheduleOne.Console;

namespace Yesla.Debugging
{
    /// <summary>
    /// Registers <c>yesla</c> as a real <c>ConsoleCommand</c>.
    ///
    /// Execution still runs through the SubmitCommand prefix - that path is proven and it swallows our word
    /// before the game ever looks it up. What registration adds is VISIBILITY: the in-game command list and the
    /// Console Autocomplete mod both build themselves from <c>Console.Commands</c>, so a command that only exists
    /// as a Harmony prefix is invisible to both. Ours was, which is exactly what a tester noticed.
    ///
    /// Only the public <c>Commands</c> list is touched. The private <c>commands</c> dictionary is what vanilla
    /// dispatch looks in, and we deliberately stay out of it: our prefix already handles dispatch, and adding a
    /// second route would risk running a command twice - which is also why the known IL2CPP problem with the
    /// abstract <c>Execute</c> never reaches us.
    /// </summary>
    internal static class ConsoleRegistration
    {
        private static bool _done;

        /// <summary>Adds the command once the game console exists. Safe to call every frame.</summary>
        internal static void EnsureRegistered()
        {
            if (_done) return;

            try
            {
                var list = GameConsole.Commands;
                if (list == null) return;   // console not up yet

                // Console.Awake only fills its stores when they are empty, so registrations survive scene changes
                // and must not be added twice.
                for (int i = 0; i < list.Count; i++)
                {
                    try
                    {
                        if (list[i] != null && string.Equals(list[i].CommandWord, DebugConsolePatch.Word, StringComparison.OrdinalIgnoreCase))
                        {
                            _done = true;
                            return;
                        }
                    }
                    catch { }
                }

                YeslaConsoleCommand cmd = new YeslaConsoleCommand();
                cmd.Configure(
                    DebugConsolePatch.Word,
                    "Yesla tooling: destinations, assisted driving, the car. 'yesla help' lists everything.",
                    DebugConsolePatch.Usage);
                list.Add(cmd);

                _done = true;
                Core.Log.Msg("[Console] registered 'yesla' for the command list and autocomplete.");
            }
            catch (Exception e)
            {
                _done = true;   // do not retry every frame on a broken build
                Core.Log.Warning("[Console] could not register the command (it still works, just without autocomplete): " + e.Message);
            }
        }
    }

    /// <summary>
    /// The console entry. Word, description and example live in managed fields, so the same injected type can back
    /// any number of commands if the mod ever needs more than one.
    /// </summary>
    [RegisterTypeInIl2Cpp]
    internal sealed class YeslaConsoleCommand : GameConsole.ConsoleCommand
    {
        private string _word = string.Empty;
        private string _description = string.Empty;
        private string _usage = string.Empty;

        public YeslaConsoleCommand(IntPtr ptr) : base(ptr) { }

        public YeslaConsoleCommand() : base(ClassInjector.DerivedConstructorPointer<YeslaConsoleCommand>())
        {
            ClassInjector.DerivedConstructorBody(this);
        }

        internal void Configure(string word, string description, string usage)
        {
            _word = word;
            _description = description;
            _usage = usage;
        }

        public override string CommandWord => _word;
        public override string CommandDescription => _description;
        public override string ExampleUsage => _usage;

        /// <summary>
        /// Fallback only. The SubmitCommand prefix normally handles this word and never lets dispatch get this far.
        /// </summary>
        public override void Execute(Il2CppSystem.Collections.Generic.List<string> args)
        {
            try
            {
                string line = _word;
                if (args != null)
                {
                    for (int i = 0; i < args.Count; i++) line += " " + args[i];
                }
                DebugConsolePatch.TryHandle(line);
            }
            catch (Exception e) { Core.Log.Warning("[Console] " + _word + " failed: " + e.Message); }
        }
    }
}
#endif
