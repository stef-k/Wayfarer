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
    public string? Read(string prompt) { Console.Write(prompt); return Console.ReadLine(); }

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
        var first = Hidden();
        Console.Write("Confirm password: ");
        if (first != Hidden()) throw new UsageException("Passwords do not match.");
        return Validate(first);
    }

    /// <summary>Accept printable characters and backspace without terminal echo.</summary>
    private static string Hidden()
    {
        var value = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
            if (key.Key == ConsoleKey.D || key.Key == ConsoleKey.C)
                if (key.Modifiers.HasFlag(ConsoleModifiers.Control)) throw new OperationCanceledException();
            if (key.Key == ConsoleKey.Backspace && value.Length > 0) value.Length--;
            else if (!char.IsControl(key.KeyChar) && value.Length < 1025) value.Append(key.KeyChar);
        }
    }

    private static string Validate(string? password) => !string.IsNullOrWhiteSpace(password) && password.Length <= 1024
        ? password : throw new UsageException("A nonempty password of at most 1024 characters is required.");
}

/// <summary>Safe operator-facing validation message, never populated from child stderr.</summary>
public sealed class UsageException(string message) : Exception(message);
