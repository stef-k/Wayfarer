namespace WayfarerCtl;

/// <summary>Shared contextual command catalogue used by both menu and direct invocation.</summary>
public static class Help
{
    public static readonly IReadOnlyDictionary<string, string> Commands = new Dictionary<string, string>
    {
        ["help"] = "help [command [subcommand]] — contextual help; aliases --help and -h",
        ["version"] = "version — CLI version (deployed Wayfarer identity is reported by status)",
        ["setup"] = "setup --bundle PATH --hostname DNS --app-digest sha256:HEX [--mode managed|external]\n" +
            "  [--project NAME] [--edge-prefix 172.30.64] [--loopback-port 8080] [--password-stdin]\n" +
            "  Interactive terminal prompts for omitted required inputs. Fresh installation only.\n" +
            "  Continue owned partial setup: setup --resume [--password-stdin] [--retry-admin]\n" +
            "  Resume verifies original config/bundle/secrets; --retry-admin explicitly retries an uncertain bootstrap.\n" +
            "  Secures the protected admin account; application password policy applies.\n" +
            "  Example: wayfarerctl setup --bundle /etc/wayfarer/releases/vX.Y.Z",
        ["status"] = "status — read-only service, health, image/version, DB and setup assessment",
        ["doctor"] = "doctor — bounded PASS/WARN/FAIL diagnosis; unhealthy checks return 1",
        ["start"] = "start — start configured services and wait for health; never update or migrate",
        ["stop"] = "stop — graceful stop (70 seconds); preserve every volume; never uninstall",
        ["restart"] = "restart — graceful stop then start and verify health; preserve state",
        ["logs"] = "logs [wayfarer|db|caddy] [--follow] [--tail N]\n  Default wayfarer, tail 100; N must be 1..10000. Caddy requires managed mode.",
        ["user"] = "user find <identity> | user reset-password <identity> [--password-stdin]\n  Exact username semantics belong to Wayfarer; no email/ID guessing or partial matches.",
        ["user find"] = "user find <identity> — delegate exact username lookup to Wayfarer; not-found returns 1",
        ["user reset-password"] = "user reset-password <identity> [--password-stdin]\n  Hidden confirmed terminal password, or a single protected redirected stdin line.\n  Example: wayfarerctl user reset-password admin --password-stdin < /root/recovery-password"
    };

    /// <summary>One global discovery/security/exit contract accompanies every command-specific page.</summary>
    public static string Text(string context)
    {
        if (context.Length > 0 && !Commands.ContainsKey(context)) throw new UsageException("Unknown help topic. Use 'wayfarerctl help'.");
        var content = context.Length == 0 ? "Commands: " + string.Join(", ", Commands.Keys.Where(key => !key.Contains(' '))) : Commands[context];
        return "wayfarerctl — Wayfarer Compose management\n" + content + "\n\n" +
            "Global prefix: --deployment-root PATH (default /etc/wayfarer; independent of cwd).\n" +
            "Examples: wayfarerctl help setup; wayfarerctl --deployment-root /etc/wayfarer status\n" +
            "Bare interactive invocation opens a line menu; redirected invocation prints help. EOF/Ctrl-C exits.\n" +
            "Exit: 0 success, 1 operation failure/cancelled/unhealthy, 2 invalid usage/config.\n" +
            "Use root and a trusted bundle. Passwords are never command arguments; protect stdin files.\n" +
            "Do not paste passwords, tokens or connection strings into options.\n" +
            "Backup, restore, update, native migration and uninstall are not implemented.";
    }
}
