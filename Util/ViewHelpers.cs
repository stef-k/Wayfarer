namespace Wayfarer.Util
{
    public static class ViewHelpers
    {
        public static string GetValueOrFallback(object value, string fallback = "N/A")
        {
            return value != null && (value is not string str || !string.IsNullOrWhiteSpace(str))
                ? value.ToString() ?? fallback
                : fallback;
        }

    }
}
