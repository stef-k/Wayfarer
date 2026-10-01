namespace WayfarerCtl;

/// <summary>Shared contextual command catalogue used by both menu and direct invocation.</summary>
public static class Help
{
    public static readonly IReadOnlyDictionary<string, string> Commands = new Dictionary<string, string>
    {
        ["help"] = "help [command [subcommand]] — contextual help; aliases --help and -h",
        ["version"] = "version — CLI version (deployed Wayfarer identity is reported by status)",
        ["release"] = "release inspect|verify-images|import|adopt /absolute/bundle\n" +
            "  target /absolute/bundle project [current|legacy]; corroborate /absolute/bundle /absolute/source.json; reconcile .stage-ID; unpack ARCHIVE|X.Y.Z /absolute/empty-private-stage.\n" +
            "  acquire X.Y.Z|latest — anonymous stable download/import and exact image pulls; no activation.",
        ["dispatch"] = "dispatch COMMAND — invoke the retained operator; restore --resume/--abort uses its receipt owner.",
        ["update"] = "update [X.Y.Z] --plan | --bundle /trusted/local/bundle --plan | --accept-plan SHA256\n" +
            "  Recovery: --resume UUID | --abort UUID (before migration only) | --restore UUID.\n" +
            "  Requires fresh held quiesced recovery. Forward-only; retained old images are not rollback. Public acquisition only prepares the existing plan.",
        ["setup"] = "setup [--version X.Y.Z | --bundle PATH] [--hostname DNS] [--mode managed|external]\n" +
            "  [--project NAME] [--edge-prefix 172.30.64] [--loopback-port 8080] [--password-stdin]\n" +
            "  Default: acquire latest public stable. --version selects one exact stable; --bundle uses a canonical local release.\n" +
            "  Setup shows progress and retries temporary download failures at most three times. Integrity/safety failures stop immediately.\n" +
            "  If setup says it has not started, correct the reported cause and run the same setup command again.\n" +
            "  After installation files exist, use setup --resume; credentials and service data are retained. Do not delete files or volumes.\n" +
            "  Image identities come from validated release.json. Interactive prompts ask for hostname/proxy/admin choices. Fresh installation only.\n" +
            "  Local raw/candidate qualification only: --bundle PATH --app-digest sha256:HEX (never a stable override).\n" +
            "  Continue owned partial setup: setup --resume [--password-stdin] [--retry-admin]\n" +
            "  Resume verifies original config/bundle/secrets; --retry-admin explicitly retries an uncertain bootstrap.\n" +
            "  Secures the protected admin account; application password policy applies.\n" +
            "  Example: sudo ./wayfarerctl setup",
        ["backup"] = "backup [--quiesced] — capture DB, complete active key ring and durable Uploads. Quiesced leaves the app stopped.",
        ["backup configure"] = "backup configure --destination PATH --payload /immutable/path/wayfarer-recovery [--kind local|mounted] [--retention 1..100] [--time HH:mm]\n  Explicit completed-installation opt-in; backup configure --disable or --recover.",
        ["backups"] = "backups — newest owned complete pairs, capped at 20; listing is not full verification.",
        ["verify-backup"] = "verify-backup [owned-basename] — non-destructive integrity and compatibility check; default newest pair.",
        ["restore"] = "restore [owned-basename] --restore-payload PATH [--plan | --accept-plan SHA256]\n" +
            "  External: --archive ABSOLUTE_ARCHIVE --source-installation UUID. SQL requires --trust-controlled-backup.\n" +
            "  New host: --new-install plus trusted setup choices, --db-digest, --capture-payload and --target-evidence.\n" +
            "  Default emergency capture; --without-emergency-backup is a plan-bound destructive waiver.\n" +
            "  Recovery: restore --resume UUID | restore --abort UUID. Abort is forbidden after possible writer launch.",
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
            "Managed restore requires an exact trusted local target. Update requires an explicit accepted plan. Native migration and uninstall are not implemented.";
    }
}
