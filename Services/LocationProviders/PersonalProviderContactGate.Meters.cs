using Microsoft.EntityFrameworkCore;
using Wayfarer.Models.LocationProviders;

namespace Wayfarer.Services.LocationProviders;

public sealed partial class PersonalProviderContactGate
{
    /// <summary>Freezes the meter before checking optional incoming authority and committing the charge.</summary>
    private async Task<PersonalProviderAdmission> AdmitGeoapifyAsync(
        string userId, PersonalProviderProduct product, int credits, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? validateMeteredAuthority = null)
    {
        await using var transaction = dbContext.Database.IsRelational() && dbContext.Database.CurrentTransaction == null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        var guard = await LockGeoapifyGuardAsync(userId, cancellationToken);
        if (validateMeteredAuthority != null) await validateMeteredAuthority(cancellationToken);
        var now = dbContext.Database.IsNpgsql()
            ? await dbContext.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken)
            : DateTimeOffset.UtcNow;
        var cutoff = now.AddHours(-24);
        var used = await dbContext.Set<GeoapifyUsageAdmission>()
            .Where(item => item.UserId == userId && item.AdmittedAt > cutoff).SumAsync(item => (int?)item.Credits, cancellationToken) ?? 0;
        if (guard.Enabled && (long)used + credits > guard.CreditLimit)
            return await CompleteAsync(PersonalProviderAdmission.Rejected(PersonalProviderAdmissionCategory.Exhausted,
                new(used, guard.CreditLimit, "credits", cutoff, null)), transaction, false, cancellationToken);

        dbContext.Set<GeoapifyUsageAdmission>().Add(new()
        { UserId = userId, Credits = credits, Product = product, AdmittedAt = now });
        var expired = dbContext.Set<GeoapifyUsageAdmission>()
            .Where(item => item.UserId == userId && item.AdmittedAt <= cutoff);
        if (dbContext.Database.IsRelational())
            await expired.ExecuteDeleteAsync(cancellationToken);
        else
            dbContext.RemoveRange(await expired.ToListAsync(cancellationToken));
        await dbContext.SaveChangesAsync(cancellationToken);
        return await CompleteAsync(new(PersonalProviderAdmissionCategory.Admitted, null,
            new(used + credits, guard.CreditLimit, "credits", cutoff, null)), transaction, true, cancellationToken);
    }

    private async Task<GeoapifyUsageGuard> LockGeoapifyGuardAsync(string userId, CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsNpgsql())
        {
            var defaultLimit = configuration.GetValue("LocationProviders:Geoapify:RollingCreditLimit", 2500);
            await dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO "GeoapifyUsageGuards" ("UserId", "Enabled", "CreditLimit")
                VALUES ({{userId}}, TRUE, {{defaultLimit}}) ON CONFLICT ("UserId") DO NOTHING
                """, cancellationToken);
            return await dbContext.Set<GeoapifyUsageGuard>().FromSqlInterpolated($$"""
                SELECT *, xmin FROM "GeoapifyUsageGuards" WHERE "UserId" = {{userId}} FOR UPDATE
                """).SingleAsync(cancellationToken);
        }
        var guard = await dbContext.Set<GeoapifyUsageGuard>().SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (guard != null) return guard;
        guard = new() { UserId = userId };
        dbContext.Add(guard);
        await dbContext.SaveChangesAsync(cancellationToken);
        return guard;
    }

    /// <summary>Freezes the meter before checking optional incoming authority and committing the charge.</summary>
    private async Task<PersonalProviderAdmission> AdmitMapboxAsync(
        string userId, PersonalProviderProduct product, int cost, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? validateMeteredAuthority = null)
    {
        await using var transaction = dbContext.Database.IsRelational() && dbContext.Database.CurrentTransaction == null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        var meter = await LockMapboxMeterAsync(userId, product, cancellationToken);
        if (validateMeteredAuthority != null) await validateMeteredAuthority(cancellationToken);
        var today = dbContext.Database.IsNpgsql()
            ? DateOnly.FromDateTime(await dbContext.Database.SqlQuery<DateTime>($"SELECT (clock_timestamp() AT TIME ZONE 'UTC') AS \"Value\"").SingleAsync(cancellationToken))
            : DateOnly.FromDateTime(DateTime.UtcNow);
        var cycle = new DateOnly(today.Year, today.Month, 1);
        if (meter.CycleStart != cycle) { meter.CycleStart = cycle; meter.AdmittedCount = 0; }
        if (meter.Enabled && (long)meter.AdmittedCount + cost > meter.Limit)
            return await CompleteAsync(PersonalProviderAdmission.Rejected(PersonalProviderAdmissionCategory.Exhausted,
                new(meter.AdmittedCount, meter.Limit, "contacts", null, cycle)), transaction, false, cancellationToken);
        meter.AdmittedCount = checked(meter.AdmittedCount + cost);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await CompleteAsync(new(PersonalProviderAdmissionCategory.Admitted, null,
            new(meter.AdmittedCount, meter.Limit, "contacts", null, cycle)), transaction, true, cancellationToken);
    }

    private async Task<MapboxProductMeter> LockMapboxMeterAsync(
        string userId, PersonalProviderProduct product, CancellationToken cancellationToken)
    {
        var key = product == PersonalProviderProduct.PermanentGeocoding ? "PermanentGeocodingLimit" : "DirectionsLimit";
        var limit = configuration.GetValue($"LocationProviders:Mapbox:{key}", 1000);
        if (dbContext.Database.IsNpgsql())
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO "MapboxProductMeters" ("UserId", "Product", "Enabled", "Limit", "CycleStart", "AdmittedCount")
                VALUES ({{userId}}, {{(int)product}}, TRUE, {{limit}}, DATE '1970-01-01', 0)
                ON CONFLICT ("UserId", "Product") DO NOTHING
                """, cancellationToken);
            return await dbContext.Set<MapboxProductMeter>().FromSqlInterpolated($$"""
                SELECT *, xmin FROM "MapboxProductMeters"
                WHERE "UserId" = {{userId}} AND "Product" = {{(int)product}} FOR UPDATE
                """).SingleAsync(cancellationToken);
        }
        var meter = await dbContext.Set<MapboxProductMeter>().SingleOrDefaultAsync(
            item => item.UserId == userId && item.Product == product, cancellationToken);
        if (meter != null) return meter;
        meter = new() { UserId = userId, Product = product, Limit = limit, CycleStart = new(1970, 1, 1) };
        dbContext.Add(meter); await dbContext.SaveChangesAsync(cancellationToken); return meter;
    }
}
