using Wayfarer.LocCheck;

var parseResult = TryParseArgs(args, out var options, out var error);
if (!parseResult)
{
    Console.Error.WriteLine(error);
    PrintUsage();
    return 2;
}

var runner = new LocCheckRunner(new SourceFileScanner());
var result = runner.Run(options);

if (result.BaselineUpdated)
{
    Console.WriteLine($"LOC baseline updated: {options.BaselinePath}");
    return 0;
}

PrintResult(result);
return result.HasFailures ? 1 : 0;

static bool TryParseArgs(string[] args, out LocCheckOptions options, out string error)
{
    var rootPath = Directory.GetCurrentDirectory();
    var baselinePath = string.Empty;
    var warningThreshold = 400;
    var failureThreshold = 600;
    var updateBaseline = false;

    for (var index = 0; index < args.Length; index++)
    {
        var arg = args[index];
        try
        {
            switch (arg)
            {
                case "--root":
                    rootPath = ReadValue(args, ref index, arg);
                    break;
                case "--baseline":
                    baselinePath = ReadValue(args, ref index, arg);
                    break;
                case "--warn":
                    if (!int.TryParse(ReadValue(args, ref index, arg), out warningThreshold))
                    {
                        options = null!;
                        error = "--warn must be an integer.";
                        return false;
                    }

                    break;
                case "--fail":
                    if (!int.TryParse(ReadValue(args, ref index, arg), out failureThreshold))
                    {
                        options = null!;
                        error = "--fail must be an integer.";
                        return false;
                    }

                    break;
                case "--update-baseline":
                    updateBaseline = true;
                    break;
                default:
                    options = null!;
                    error = $"Unknown argument: {arg}";
                    return false;
            }
        }
        catch (ArgumentException exception)
        {
            options = null!;
            error = exception.Message;
            return false;
        }
    }

    if (warningThreshold <= 0 || failureThreshold <= 0)
    {
        options = null!;
        error = "Thresholds must be positive integers.";
        return false;
    }

    if (warningThreshold > failureThreshold)
    {
        options = null!;
        error = "--warn must be less than or equal to --fail.";
        return false;
    }

    rootPath = Path.GetFullPath(rootPath);
    baselinePath = string.IsNullOrWhiteSpace(baselinePath)
        ? Path.Combine(rootPath, "tools", "Wayfarer.LocCheck", "loc-baseline.json")
        : Path.GetFullPath(Path.IsPathRooted(baselinePath) ? baselinePath : Path.Combine(rootPath, baselinePath));

    options = new LocCheckOptions
    {
        RootPath = rootPath,
        BaselinePath = baselinePath,
        WarningThreshold = warningThreshold,
        FailureThreshold = failureThreshold,
        UpdateBaseline = updateBaseline
    };
    error = string.Empty;
    return true;
}

static string ReadValue(string[] args, ref int index, string optionName)
{
    if (index + 1 >= args.Length)
    {
        throw new ArgumentException($"Missing value for {optionName}");
    }

    index++;
    return args[index];
}

static void PrintResult(LocCheckResult result)
{
    if (result.Files.Count == 0)
    {
        Console.WriteLine("LOC check passed with no warnings.");
        return;
    }

    foreach (var file in result.Files)
    {
        var prefix = file.Severity == LocSeverity.Failure ? "FAIL" : "WARN";
        var baseline = file.BaselineLines is null ? "new" : $"baseline {file.BaselineLines}";
        Console.WriteLine($"{prefix}: {file.Path} ({file.Lines} LOC, {baseline}) - {file.Message}");
    }
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/Wayfarer.LocCheck -- [--warn 400] [--fail 600] [--update-baseline]");
}
