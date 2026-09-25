using System.Text.Json;
using System.Text.RegularExpressions;

namespace WayfarerCtl;

/// <summary>Persisted non-secret discovery/config identity; unrelated cwd/environment never selects state.</summary>
public sealed record Deployment
{
    public int Schema { get; init; } = 1;
    public string Bundle { get; init; } = "";
    public string Project { get; init; } = "wayfarer";
    public string Hostname { get; init; } = "";
    public string Mode { get; init; } = "managed";
    public string AppDigest { get; init; } = "";
    public string DbDigest { get; init; } = "sha256:bd9b3bbfe1e879b56b0742646c18d0dcc9ec95180095f8f6d02e03b54feeeb61";
    public string EdgePrefix { get; init; } = "172.30.64";
    public int LoopbackPort { get; init; } = 8080;

    public static readonly string[] EnvironmentKeys = ["PUBLIC_HOST", "WAYFARER_DIGEST", "DB_DIGEST", "PROXY_MODE",
        "EDGE_PREFIX", "DB_PASSWORD_FILE", "DB_APP_PASSWORD_FILE", "APP_PASSWORD_FILE", "EXTERNAL_PROXY_ADDRESS",
        "LOOPBACK_ADDRESS", "LOOPBACK_PORT"];

    /// <summary>Fail closed on unknown schema, identities, input expansion and unsupported proxy topology.</summary>
    public void Validate()
    {
        if (Schema != 1 || !Path.IsPathFullyQualified(Bundle) || Bundle.IndexOfAny(['\n', '\r', '$', '"', '\'','`']) >= 0)
            throw new UsageException("Invalid installation schema or absolute bundle path.");
        if (!Regex.IsMatch(Project, "^[a-z0-9][a-z0-9_-]{0,62}$")) throw new UsageException("Invalid project identity.");
        if (!Regex.IsMatch(AppDigest, "^sha256:[a-f0-9]{64}$") || !Regex.IsMatch(DbDigest, "^sha256:[a-f0-9]{64}$"))
            throw new UsageException("Immutable sha256 application and database digests are required.");
        if (Mode is not ("managed" or "external")) throw new UsageException("Proxy mode must be managed or external.");
        if (Hostname.Length > 253 || !Regex.IsMatch(Hostname, "^[a-zA-Z0-9]([a-zA-Z0-9.-]*[a-zA-Z0-9])?$") ||
            !Hostname.Contains('.') || Hostname.Split('.').Any(label => label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-')) ||
            !Hostname.Split('.').Last().Any(char.IsAsciiLetter) ||
            new[] { ".localhost", ".local", ".internal", ".home.arpa", ".test", ".invalid", ".example", ".onion", ".alt" }
                .Any(suffix => Hostname.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            throw new UsageException("A public DNS hostname is required.");
        if (!Regex.IsMatch(EdgePrefix, @"^172\.(1[6-9]|2[0-9]|3[01])\.(0|[1-9][0-9]?|1[0-9]{2}|2[0-4][0-9]|25[0-5])$") ||
            LoopbackPort is < 1 or > 65535) throw new UsageException("Invalid private edge /24 or loopback port.");
    }

    /// <summary>Inspect bundle inputs before any Compose operation; no script execution is needed.</summary>
    public void CheckBundle()
    {
        Validate();
        foreach (var file in new[] { "compose.yaml", "external.yaml", "caddy/Caddyfile", "db/20-wayfarer.sh", "config/deployment.env.example" })
        {
            var path = Path.Combine(Bundle, file);
            ProtectedFiles.SafePath(path);
            if (!File.Exists(path)) throw new UsageException("Required bundle file missing; obtain the complete trusted bundle.");
        }
    }

    /// <summary>Read the one explicit installation identity and verify its generated Compose input.</summary>
    public static Deployment Load(string root)
    {
        ProtectedFiles.SafePath(root);
        ProtectedFiles.Check(root, 0, directory: true);
        var path = Path.Combine(root, "installation.json");
        ProtectedFiles.Check(path, 0);
        var config = JsonSerializer.Deserialize<Deployment>(File.ReadAllText(path), new JsonSerializerOptions
        { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
            ?? throw new UsageException("Missing installation identity.");
        config.CheckBundle();
        ProtectedFiles.Check(Path.Combine(root, "deployment.env"), 0);
        if (File.ReadAllText(Path.Combine(root, "deployment.env")) != config.EnvironmentFile(root))
            throw new UsageException("Configuration differs from installation identity; reconcile it explicitly before operation.");
        return config;
    }

    /// <summary>Literal non-secret interpolation file; credentials are always bounded mounted files.</summary>
    public string EnvironmentFile(string root) => $"PUBLIC_HOST={Hostname}\nWAYFARER_DIGEST={AppDigest}\nDB_DIGEST={DbDigest}\n" +
        $"PROXY_MODE={Mode}\nEDGE_PREFIX={EdgePrefix}\nDB_PASSWORD_FILE={root}/secrets/db-password\n" +
        $"DB_APP_PASSWORD_FILE={root}/secrets/db-app-password\nAPP_PASSWORD_FILE={root}/secrets/app-password\n" +
        $"EXTERNAL_PROXY_ADDRESS={EdgePrefix}.1\nLOOPBACK_ADDRESS=127.0.0.1\nLOOPBACK_PORT={LoopbackPort}\n";

    /// <summary>Fixed project and explicit mode prevent ambient Compose files/profiles selecting resources.</summary>
    public string[] Compose(string root, params string[] arguments)
    {
        var prefix = new List<string> { "compose", "--project-name", Project, "--project-directory", Bundle,
            "--env-file", Path.Combine(root, "deployment.env"), "-f", Path.Combine(Bundle, "compose.yaml") };
        if (Mode == "managed") prefix.AddRange(["--profile", "managed"]);
        else prefix.AddRange(["-f", Path.Combine(Bundle, "external.yaml")]);
        return [.. prefix, .. arguments];
    }

    /// <summary>Validate all consumer copies and their common private parent without exposing bytes.</summary>
    public static void CheckSecrets(string root)
    {
        var directory = Path.Combine(root, "secrets");
        ProtectedFiles.Check(directory, 0, directory: true);
        foreach (var (name, owner) in new[] { ("db-password", 0u), ("db-app-password", 999u), ("app-password", 1654u) })
        {
            var path = Path.Combine(directory, name);
            ProtectedFiles.Check(path, owner);
            if (!Regex.IsMatch(File.ReadAllText(path), "^[A-F0-9]{64}$")) throw new UsageException("Invalid generated secret material.");
        }
        var app = File.ReadAllText(Path.Combine(directory, "app-password"));
        if (app != File.ReadAllText(Path.Combine(directory, "db-app-password")) || app == File.ReadAllText(Path.Combine(directory, "db-password")))
            throw new UsageException("Credential copies do not match the distinct-role contract.");
    }
}
