using System.Windows.Input;

namespace AirServerLite.Input;

/// <summary>
/// Maps WPF keys onto the Unicode private-use codepoints that WebDriverAgent (following the
/// WebDriver spec) interprets as special keys. Printable characters do not go through here -
/// they arrive as TextInput events, which already handle layout, dead keys and IME
/// composition correctly. Trying to reconstruct characters from Key values instead is how you
/// end up with an app that only types correctly on a US keyboard.
/// </summary>
public static class KeyMap
{
    // WebDriver "\uE00x" special key codepoints.
    private const string Backspace = "\uE003";
    private const string Tab = "\uE004";
    private const string Return = "\uE006";
    private const string Enter = "\uE007";
    private const string Escape = "\uE00C";
    private const string Space = "\uE00D";
    private const string PageUp = "\uE00E";
    private const string PageDown = "\uE00F";
    private const string End = "\uE010";
    private const string Home = "\uE011";
    private const string ArrowLeft = "\uE012";
    private const string ArrowUp = "\uE013";
    private const string ArrowRight = "\uE014";
    private const string ArrowDown = "\uE015";
    private const string Delete = "\uE017";

    public static string? ToWdaKey(Key key) => key switch
    {
        Key.Back => Backspace,
        Key.Delete => Delete,
        Key.Tab => Tab,
        Key.Return => Return, // Key.Enter is the same value in WPF
        Key.Escape => Escape,
        Key.PageUp => PageUp,
        Key.PageDown => PageDown,
        Key.End => End,
        Key.Home => Home,
        Key.Left => ArrowLeft,
        Key.Up => ArrowUp,
        Key.Right => ArrowRight,
        Key.Down => ArrowDown,
        _ => null
    };

    public static string Describe(Key key) => key switch
    {
        Key.Back => "Backspace",
        Key.Delete => "Delete",
        Key.Return => "Enter",
        Key.Escape => "Escape",
        Key.Tab => "Tab",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.Up => "Up",
        Key.Down => "Down",
        _ => key.ToString()
    };
}
