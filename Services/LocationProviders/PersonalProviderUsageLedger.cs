using Wayfarer.Models.LocationProviders;

namespace Wayfarer.Services.LocationProviders;

/// <summary>Provides deterministic in-memory semantics used by callers and unit tests; PostgreSQL owns durable admission.</summary>
public sealed class PersonalProviderUsageLedger
{
    private readonly List<(DateTimeOffset At, int Credits)> _geoapify = [];
    private readonly Dictionary<(DateOnly Cycle, PersonalProviderProduct Product), int> _mapbox = [];

    /// <summary>Atomically admits positive credits against one shared rolling pool.</summary>
    public bool TryAdmitGeoapify(DateTimeOffset now, int limit, int credits, PersonalProviderProduct product)
    {
        if (credits <= 0 || limit < 0 || product is not (PersonalProviderProduct.Geocoding or PersonalProviderProduct.Routing))
            return false;
        lock (_geoapify)
        {
            _geoapify.RemoveAll(item => item.At <= now.AddHours(-24));
            if (_geoapify.Sum(item => item.Credits) + credits > limit) return false;
            _geoapify.Add((now, credits));
            return true;
        }
    }

    /// <summary>Atomically admits one Mapbox contact against its independent product cycle.</summary>
    public bool TryAdmitMapbox(DateOnly cycle, PersonalProviderProduct product, int limit, int cost)
    {
        if (cost <= 0 || limit < 0 || product is not (PersonalProviderProduct.PermanentGeocoding or PersonalProviderProduct.Directions))
            return false;
        lock (_mapbox)
        {
            var key = (cycle, product);
            var used = _mapbox.GetValueOrDefault(key);
            if (used + cost > limit) return false;
            _mapbox[key] = used + cost;
            return true;
        }
    }
}
