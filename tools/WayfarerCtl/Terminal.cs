namespace WayfarerCtl;

/// <summary>Small console seam shared by direct commands and the line-oriented menu.</summary>
public interface ITerminal
{
    bool Interactive { get; }
    void Write(string message);
    void Error(string message);
    string? Read(string prompt);
    string Password(bool fromStdin);
}

/// <summary>Never echoes protected input and treats EOF as cancellation.</summary>
public sealed class Terminal : ITerminal
{
    public bool Interactive => !Console.IsInputRedirected && !Console.IsOutputRedirected;
    public void Write(string message) => Console.WriteLine(message);
    public void Error(string message) => Console.Error.WriteLine(message);
    /// <summary>Line editing stays cancellable even while the menu is waiting for a key.</summary>
    public string? Read(string prompt) { Console.Write(prompt); return ReadKeys(hidden: false); }

    /// <summary>Automation deliberately redirects one password line; menus hide and confirm it.</summary>
    public string Password(bool fromStdin)
    {
        if (fromStdin)
        {
            if (!Console.IsInputRedirected) throw new UsageException("--password-stdin requires redirected input.");
            return Validate(Console.ReadLine());
        }
        if (!Interactive) throw new UsageException("Use --password-stdin with protected redirected input.");
        Console.Write("Password: ");
        var first = ReadKeys(hidden: true) ?? throw new OperationCanceledException();
        Console.Write("Confirm password: ");
        if (first != ReadKeys(hidden: true)) throw new UsageException("Passwords do not match.");
        return Validate(first);
    }

    /// <summary>Handle Ctrl-C/Ctrl-D explicitly so interactive reads never strand cancellation.</summary>
    private static string? ReadKeys(bool hidden)
    {
        var original = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try
        {
            var value = new System.Text.StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
                if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C)
                    throw new OperationCanceledException();
                if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.D)
                    return null;
                if (key.Key == ConsoleKey.Backspace && value.Length > 0)
                {
                    value.Length--;
                    if (!hidden) Console.Write("\b \b");
                }
                else if (!char.IsControl(key.KeyChar) && value.Length < 1025)
                {
                    value.Append(key.KeyChar);
                    if (!hidden) Console.Write(key.KeyChar);
                }
            }
        }
        finally { Console.TreatControlCAsInput = original; }
    }

    private static string Validate(string? password) => !string.IsNullOrWhiteSpace(password) && password.Length <= 1024
        ? password : throw new UsageException("A nonempty password of at most 1024 characters is required.");
}

/// <summary>Safe operator-facing validation message, never populated from child stderr.</summary>
public sealed class UsageException(string message) : Exception(message);
