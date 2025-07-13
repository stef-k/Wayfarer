namespace Wayfarer.Parsers;

internal static class KmlMappings
{
    public static readonly IReadOnlyDictionary<string, string> IconMapping = new Dictionary<string, string>
    {
        ["anchor"] = "anchor",
        ["atm"] = "atm",
        ["barbecue"] = "barbecue", // cooking grill
        ["beach"] = "beachflag",
        ["bike"] = "bicycle",
        ["boat"] = "boat",
        ["camera"] = "camera",
        ["camping"] = "campground",
        ["car"] = "car",
        ["charging-point"] = "charging", // electric vehicle
        ["checkmark"] = "checkered_flag", // ✔️
        ["clouds"] = "clouds",
        ["construction"] = "construction",
        ["danger"] = "danger",
        ["drink"] = "bar",
        ["eat"] = "restaurant",
        ["ev-station"] = "charging", // same as charging-point
        ["fitness"] = "running_man",
        ["flag"] = "target",
        ["flight"] = "airport",
        ["gas"] = "gas_station",
        ["help"] = "help",
        ["hike"] = "hiker",
        ["hospital"] = "hospital",
        ["hotel"] = "lodging",
        ["info"] = "info_i",
        ["kayak"] = "boat", // approximate
        ["latest"] = "placemark_circle", // generic
        ["luggage"] = "baggage_claim",
        ["map"] = "map",
        ["museum"] = "museum",
        ["no-wheelchair"] = "no_wheelchair",
        ["no-wifi"] = "no_wifi",
        ["park"] = "park",
        ["parking"] = "parking_lot",
        ["pet"] = "pet_store",
        ["pharmacy"] = "pharmacy",
        ["phishing"] = "danger", // approximate
        ["police"] = "police",
        ["run"] = "running_man",
        ["sail"] = "boat",
        ["scuba-dive"] = "diving",
        ["sea"] = "water",
        ["shopping"] = "shopping_cart",
        ["ski"] = "ski",
        ["smoke-free"] = "smoke_free",
        ["smoke"] = "smoking_area",
        ["sos"] = "emergency",
        ["star"] = "star",
        ["subway"] = "rail_subway",
        ["surf"] = "surfing",
        ["swim"] = "swimming",
        ["taxi"] = "taxi",
        ["telephone"] = "telephone",
        ["thunderstorm"] = "storm",
        ["tool"] = "tool",
        ["train"] = "rail",
        ["walk"] = "walking",
        ["water"] = "water",
        ["wc"] = "toilets",
        ["wheelchair"] = "wheelchair_accessible",
        ["wifi"] = "wifi"
    };

    public static readonly IReadOnlyDictionary<string, string> ColorMapping = new Dictionary<string, string>
    {
        // CSS: #000000 → AABBGGRR = ff 00 00 00
        ["bg-black"] = "ff000000",

        // CSS: #6f42c1 →            = ff c1 42 6f
        ["bg-purple"] = "ffc1426f",

        // CSS: #0d6efd →            = ff fd 6e 0d
        ["bg-blue"] = "fffd6e0d",

        // CSS: #198754 →            = ff 54 87 19
        ["bg-green"] = "ff548719",

        // CSS: #dc3545 →            = ff 45 35 dc
        ["bg-red"] = "ff4535dc"
    };
}