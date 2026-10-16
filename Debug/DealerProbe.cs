#if DEBUG
using System;
using Il2CppScheduleOne.Dialogue;

namespace Yesla.Debugging
{
    /// <summary>
    /// Walks Jeremy's sell conversation from the console: <c>yesla dealer</c> opens it, <c>yesla dealer pick N</c>
    /// selects choice N, <c>yesla dealer end</c> closes it. Every step logs the node and its choices.
    /// </summary>
    internal static class DealerProbe
    {
        internal static void Run(string[] parts)
        {
            var handler = UnityEngine.Object.FindObjectOfType<DialogueHandler_VehicleSalesman>();
            if (handler == null) { Say("no vehicle salesman in the scene"); return; }
            string verb = parts.Length > 2 ? parts[2].ToLower() : "open";

            if (verb == "pick")
            {
                if (parts.Length < 4 || !int.TryParse(parts[3], out int index)) { Say("usage: yesla dealer pick <n>"); return; }
                handler.ChoiceSelected(index);
                List(handler);
                return;
            }
            if (verb == "end") { handler.EndDialogue(); Say("dealer: conversation ended"); return; }

            // The sell conversation is an asset referenced from the scene, not from the controller's choice list.
            Conversation sell = null;
            foreach (var c in Resources.FindObjectsOfTypeAll<Conversation>())
            {
                if (c == null || c.name.IndexOf("Salesman", StringComparison.OrdinalIgnoreCase) < 0) continue;
                Say("dealer: conversation '" + c.name + "'");
                if (sell == null && c.name.IndexOf("Sell", StringComparison.OrdinalIgnoreCase) >= 0) sell = c;
            }
            if (sell == null) { Say("no sell conversation loaded"); return; }
            handler.StartDialogue(sell, false, "ENTRY");
            List(handler);
        }

        private static void List(DialogueHandler handler)
        {
            var node = DialogueHandler.ActiveDialogueNode;
            if (node == null) { Say("dealer: no active node (conversation over)"); return; }
            Say("dealer: node '" + node.DialogueNodeLabel + "': " + node.DialogueText);
            var choices = handler.finalChoices;
            for (int i = 0; i < choices.Count; i++) Say("dealer:   [" + i + "] " + choices[i].ChoiceLabel + " = " + choices[i].ChoiceText);
        }

        private static void Say(string s) => Core.Log.Msg(s);
    }
}
#endif
