using GiftDeck.Models;

namespace GiftDeck.Views;

// A window that shows a list of ActionEditors (the event editor, a spinner prize's actions),
// so an editor's up/down/remove buttons work wherever it is shown.
public interface IActionHost
{
    void MoveAction(RuleAction a, int delta);
    void RemoveAction(RuleAction a);
}
