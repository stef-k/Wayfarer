using System.Reflection;

namespace WayfarerCtl;

/// <summary>Dispatches validated commands; menus re-enter this same handler path.</summary>
public sealed class Cli(IProcessRunner runner, ITerminal terminal)
{
    /// <summary>Translate failures without printing potentially secret-bearing exception/child text.</summary>
    public async Task<int> RunAsync(string[] args, CancellationToken token = default)
    {
        try { return await DispatchAsync(args, token); }
        catch (UsageException e) { terminal.Error(e.Message); return 2; }
        catch (OperationCanceledException) { terminal.Error("Cancelled. State retained; run status/doctor before retrying."); return 1; }
        catch (Exception) { terminal.Error("Operation failed. State retained; check Docker access, protected configuration and doctor."); return 1; }
    }

    private async Task<int> DispatchAsync(string[] args, CancellationToken token)
    {
        var root = "/etc/wayfarer";
        if (args is ["--deployment-root", var selected, ..])
        {
            if (!Path.IsPathFullyQualified(selected) || selected.IndexOfAny(['\n', '\r', '$', '"', '\'', '`']) >= 0)
                throw new UsageException("--deployment-root requires a literal absolute path.");
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected));
            args = args[2..];
        }
        if (args.Length == 0)
        {
            if (terminal.Interactive) return await MenuAsync(root, token);
            terminal.Write(Help.Text("")); return 0;
        }
        if (args[0] == "help" || args[^1] is "--help" or "-h")
        {
            var context = args[0] == "help" ? string.Join(' ', args[1..]) : string.Join(' ', args[..^1]);
            terminal.Write(Help.Text(context)); return 0;
        }
        if (args is ["version"])
        {
            terminal.Write("wayfarerctl " + typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion +
                "\nDeployed Wayfarer: use status (independent image identity)."); return 0;
        }
        ValidateCommand(args);
        Preflight.Platform();
        ProtectedFiles.RequireRoot();
        if (args[0] == "setup") return await new Setup(runner, terminal).RunAsync(root, args[1..], token);
        var config = Deployment.Load(root);
        if (args[0] is "status" or "doctor")
            return await new Diagnostics(runner, terminal).RunAsync(root, config, args[0] == "doctor", token);
        Deployment.CheckSecrets(root);
        await new Preflight(runner).DockerAsync(token);
        if (args[0] == "logs") return await LogsAsync(root, config, args[1..], token);
        if (!File.Exists(Path.Combine(root, "setup-complete")))
            throw new UsageException("Setup incomplete; follow interrupted-setup recovery before lifecycle/user operations.");
        using var operationLock = Setup.Lock(root);
        if (args[0] == "user") return await UserAsync(root, config, args, token);
        return await LifecycleAsync(root, config, args[0], token);
    }

    /// <summary>Validate all direct argument forms before touching deployment state.</summary>
    public static void ValidateCommand(string[] args)
    {
        if (args is ["setup", ..]) { Setup.Options(args[1..]); return; }
        if (args is ["status" or "doctor" or "start" or "stop" or "restart"]) return;
        if (args is ["logs", ..]) { LogOptions(args[1..]); return; }
        if (args is ["user", "find", var identity] && ValidIdentity(identity)) return;
        if (args is ["user", "reset-password", var name] && ValidIdentity(name)) return;
        if (args is ["user", "reset-password", var user, "--password-stdin"] && ValidIdentity(user)) return;
        var hint = args[0] == "user" ? "user" : Help.Commands.ContainsKey(args[0]) ? args[0] : "";
        throw new UsageException($"Invalid command/options. Use 'wayfarerctl help {hint}'.");
    }

    private static bool ValidIdentity(string value) => value.Length is > 0 and <= 256 && !value.StartsWith('-') && !value.Any(char.IsControl);

    /// <summary>The menu supplies command arguments only, keeping maintenance logic in common handlers.</summary>
    private async Task<int> MenuAsync(string root, CancellationToken token)
    {
        var entries = new[] { "setup", "status", "doctor", "start", "stop", "restart", "logs", "user", "help" };
        while (!token.IsCancellationRequested)
        {
            terminal.Write("1 Setup  2 Status  3 Doctor  4 Start  5 Stop  6 Restart  7 Logs  8 User recovery  9 Help  0 Exit");
            var choice = terminal.Read("> ");
            if (choice is null or "0") return 0;
            if (!int.TryParse(choice, out var index) || index < 1 || index > entries.Length) { terminal.Error("Choose 0..9."); continue; }
            string[] command = [entries[index - 1]];
            if (command[0] == "user")
            {
                var operation = terminal.Read("1 Find  2 Reset password  (Enter to return): ");
                if (operation is not ("1" or "2")) continue;
                var identity = terminal.Read("Exact username: ");
                if (identity is null) return 0;
                command = ["user", operation == "1" ? "find" : "reset-password", identity];
            }
            await RunAsync(["--deployment-root", root, .. command], token);
        }
        return 1;
    }

    /// <summary>No pull, recreate, migration or volume deletion is hidden inside ordinary lifecycle.</summary>
    private async Task<int> LifecycleAsync(string root, Deployment config, string operation, CancellationToken token)
    {
        if (operation is "stop" or "restart")
            await RequiredAsync(config.Compose(root, "stop", "--timeout", "70"), null, token);
        if (operation != "stop")
        {
            await RequiredAsync(config.Compose(root, "up", "-d", "--no-recreate", "--pull", "never", "--wait", "--wait-timeout", "180"), null, token);
            return await new Diagnostics(runner, terminal).RunAsync(root, config, true, token);
        }
        terminal.Write("Stopped. All durable volumes retained."); return 0;
    }

    private async Task<int> UserAsync(string root, Deployment config, string[] args, CancellationToken token)
    {
        var reset = args[1] == "reset-password";
        var password = reset ? terminal.Password(args.Contains("--password-stdin")) : null;
        var command = reset ? new[] { "admin", "reset", args[2], "--stdin" } : new[] { "user", "find", args[2] };
        var result = await runner.RunAsync(config.Compose(root, ["run", "--rm", "--no-deps", "-T", "wayfarer", .. command]), password, token);
        if (result.Code != 0) { terminal.Error("User operation failed: verify exact username, database and password policy. No identity guessing was performed."); return 1; }
        terminal.Write(reset ? "Password reset completed." : result.Output.Trim());
        return 0;
    }

    public static (string Service, int Tail, bool Follow) LogOptions(string[] args)
    {
        var service = "wayfarer"; var tail = 100; var follow = false;
        if (args.Length > 0 && !args[0].StartsWith('-')) { service = args[0]; args = args[1..]; }
        if (service is not ("wayfarer" or "db" or "caddy")) throw new UsageException("Unknown log service. Use 'wayfarerctl help logs'.");
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--follow" && !follow) follow = true;
            else if (args[i] == "--tail" && ++i < args.Length && int.TryParse(args[i], out var number) && number is >= 1 and <= 10000) tail = number;
            else throw new UsageException("Invalid logs option. Use 'wayfarerctl help logs'.");
        }
        return (service, tail, follow);
    }

    private async Task<int> LogsAsync(string root, Deployment config, string[] args, CancellationToken token)
    {
        var (service, tail, follow) = LogOptions(args);
        if (service == "caddy" && config.Mode != "managed") throw new UsageException("Caddy is absent in external mode.");
        var options = new List<string> { "logs", "--no-color", "--tail", tail.ToString() };
        if (follow) options.Add("--follow");
        options.Add(service);
        var result = await runner.RunAsync(config.Compose(root, options.ToArray()), null, token);
        terminal.Write(result.Output);
        return result.Code == 0 ? 0 : 1;
    }

    private async Task RequiredAsync(string[] args, string? input, CancellationToken token)
    {
        if ((await runner.RunAsync(args, input, token)).Code != 0) throw new IOException("Docker operation failed.");
    }
}
