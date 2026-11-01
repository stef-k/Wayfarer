using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Models;
using Wayfarer.Models.Planning;
using Wayfarer.Parsers;

namespace Wayfarer.Services;

/// <summary>One explicit per-target intent, signed against the file and complete current originals.</summary>
public sealed record NativeImportChoice(string Action, string Token);

/// <summary>Owner-only whole-aggregate comparison; a removed row requires affirmative loss consent.</summary>
public sealed record NativeImportPlanningRow(string Key, string Kind, string Name, string State,
    PlanningAggregate? Current, PlanningAggregate? Imported, IReadOnlyList<NativeImportChoice> Choices);

/// <summary>Read-only replacement preview. Its permits convey no authority without a current owned target.</summary>
public sealed record NativeImportReview(string Token, Guid TripId, string TargetName, string ImportedName,
    IReadOnlyList<NativeImportPlanningRow> Planning);

/// <summary>Exactly one selected permit per reviewed target; Keep is the default for retained planning.</summary>
public sealed record NativeImportApproval(string Token, IReadOnlyList<string> Choices);

/// <summary>Known refusal: the file and choices may be retained for a fresh review.</summary>
public sealed class NativeImportReviewException(string code) : Exception("Review the current Trip before applying this update.")
{
    /// <summary>Safe stable error code, without current values or foreign identity information.</summary>
    public string Code { get; } = code;
}

/// <summary>Commit confirmation failed; rollback cannot establish whether the server applied the import.</summary>
public sealed class NativeImportOutcomeUnknownException() : Exception("The import outcome could not be confirmed.");

/// <summary>Small native-import permit codec, using the existing application key ring, without a receipt ledger.</summary>
internal sealed class NativeImportReviewProtection(IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("Wayfarer.NativeImport.Review.v1");
    private sealed record Permit(string Binding, string Key, string Action, DateTimeOffset Expires);

    /// <summary>Includes absent aggregates, membership, all reusable material and exact saved measurements.</summary>
    internal static string Binding(Trip current, string owner, string digest) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { owner, digest, mode = "Upsert", current.Id,
            current.IsPublic, Content = TripWayfarerKmlExporter.BuildKml(current, true) }))));

    internal string Issue(string binding, string key, string action) => protector.Protect(JsonSerializer.Serialize(
        new Permit(binding, key, action, DateTimeOffset.UtcNow.AddMinutes(20))));

    /// <summary>Rejects stale, substituted, expired or malformed review/choice permits generically.</summary>
    internal (string Key, string Action) Read(string? token, string binding)
    {
        try
        {
            if (token == null || token.Length > 8192) throw new CryptographicException();
            var value = JsonSerializer.Deserialize<Permit>(protector.Unprotect(token));
            if (value == null || value.Binding != binding || value.Expires < DateTimeOffset.UtcNow) throw new CryptographicException();
            return (value.Key, value.Action);
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException)
        {
            throw new NativeImportReviewException("import_review_stale");
        }
    }

    /// <summary>Comparison rows expose conflicts only to the authenticated owner of this exact graph.</summary>
    internal NativeImportReview Preview(Trip current, Trip incoming, string binding)
    {
        var next = NativeTripGraph.Targets(incoming).ToDictionary(t => Key(t.Kind, t.Id));
        List<NativeImportPlanningRow> rows = [];
        foreach (var item in NativeTripGraph.Targets(current))
        {
            var key = Key(item.Kind, item.Id);
            var retained = next.Remove(key, out var target);
            var before = item.Target.Planning;
            var after = retained ? target.Target.Planning : null;
            if (before == null && after == null) continue;
            var state = !retained ? "removed" : before == null ? "absent" : after == null ? "omitted"
                : PlanningCodec.Equal(before, after) ? "equal" : "conflict";
            string[] actions = state == "removed" ? ["remove"] : before == null ? []
                : state == "conflict" ? ["keep", "use", "clear"] : ["keep", "clear"];
            rows.Add(new(key, item.Kind, item.Name, state, before, after,
                actions.Select(action => new NativeImportChoice(action, Issue(binding, key, action))).ToArray()));
        }
        rows.AddRange(next.Where(t => t.Value.Target.Planning != null).Select(t =>
            new NativeImportPlanningRow(t.Key, t.Value.Kind, t.Value.Name, "new", null, t.Value.Target.Planning, [])));
        return new(Issue(binding, "", "review"), current.Id, current.Name, incoming.Name, rows);
    }

    /// <summary>Validates all explicit choices together before mutating even the detached destination graph.</summary>
    internal void Apply(Trip current, Trip incoming, string binding, NativeImportApproval? approval)
    {
        if (approval == null) throw new NativeImportReviewException("import_review_required");
        if (Read(approval.Token, binding) != ("", "review") || approval.Choices == null)
            throw new NativeImportReviewException("import_review_stale");
        var preview = Preview(current, incoming, binding);
        var rows = preview.Planning.ToDictionary(row => row.Key);
        var selected = new Dictionary<string, string>();
        foreach (var token in approval.Choices)
        {
            var (key, action) = Read(token, binding);
            if (!rows.TryGetValue(key, out var row) || !row.Choices.Any(c => c.Action == action) || !selected.TryAdd(key, action))
                throw new NativeImportReviewException("import_choices_invalid");
        }
        foreach (var row in preview.Planning.Where(row => row.State == "removed"))
            if (!selected.TryGetValue(row.Key, out var action) || action != "remove")
                throw new NativeImportReviewException("import_loss_consent_required");
        var previous = NativeTripGraph.Targets(current).ToDictionary(t => Key(t.Kind, t.Id));
        foreach (var item in NativeTripGraph.Targets(incoming))
        {
            var key = Key(item.Kind, item.Id);
            if (!previous.TryGetValue(key, out var prior))
            {
                item.Target.Planning = WayfarerKmlAggregateMapper.CopyPlanning(item.Target.Planning, true);
                continue;
            }
            if (selected.GetValueOrDefault(key) == "clear") item.Target.Planning = null;
            else if (prior.Target.Planning != null && selected.GetValueOrDefault(key) != "use")
                item.Target.Planning = prior.Target.Planning;
        }
    }

    private static string Key(string kind, Guid id) => $"{kind}/{id:D}";
}
