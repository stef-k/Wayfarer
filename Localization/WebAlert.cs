using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Wayfarer.Localization;

/// <summary>Stores and renders site alerts with destination-locale text and safe display arguments.</summary>
internal static class WebAlert
{
    private const string ArgumentsKey = "WebAlertArguments";
    private const string NumbersKey = "WebAlertNumbers";

    /// <summary>Writes one alert using types supported by the MVC TempData cookie provider.</summary>
    internal static void Store(ITempDataDictionary data, string message, string type, string? key,
        string[]? arguments = null, int[]? numbers = null)
    {
        data["AlertMessage"] = message;
        data["AlertType"] = type;
        data["WebAlertKey"] = key;
        data[ArgumentsKey] = arguments;
        data[NumbersKey] = numbers;
        data.Remove("WebAlertArgument");
        data.Remove("WebAlertCount");
    }

    /// <summary>Formats current arguments and keeps the existing single-argument and plural alerts readable.</summary>
    internal static (string? Text, bool Localized) Read(ITempDataDictionary data, WebMessages text)
    {
        var key = data["WebAlertKey"] as string;
        var legacy = data["AlertMessage"] as string;
        var argument = data["WebAlertArgument"];
        var count = data["WebAlertCount"];
        var arguments = data[ArgumentsKey] as IEnumerable<string>;
        var numbers = data[NumbersKey] as IEnumerable<int>;
        if (key is null)
            return (legacy, false);
        if (count is int total)
            return (text.Count(key, total), true);
        if (numbers is not null)
            return (text[key, numbers.Select(number => text.DisplayNumber(number)).ToArray()], true);
        if (arguments is not null)
            return (text[key, arguments.ToArray()], true);
        return (text[key, argument], true);
    }
}
