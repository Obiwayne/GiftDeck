namespace GiftDeck.Views;

// A value with a friendly label, for dropdowns.
public class Choice
{
    public Choice(object value, string label) { Value = value; Label = label; }
    public object Value { get; }
    public string Label { get; }
    public override string ToString() => Label;
}
