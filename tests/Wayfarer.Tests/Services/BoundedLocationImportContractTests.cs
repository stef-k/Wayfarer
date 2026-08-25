using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Defines the bounded ownership contract for the location-import worker.</summary>
public sealed class BoundedLocationImportContractTests
{
    [Fact]
    public void ParserContract_StreamsLocationsInsteadOfReturningCompleteList()
    {
        var parse = typeof(ILocationDataParser).GetMethod("ParseAsync");

        Assert.NotNull(parse);
        Assert.Equal(typeof(IAsyncEnumerable<Location>), parse!.ReturnType);
        Assert.Contains(parse.GetParameters(), parameter =>
            parameter.ParameterType == typeof(CancellationToken));
    }

    [Fact]
    public void ImportWorker_ReceivesFactoryForPhaseScopedContexts()
    {
        var constructors = typeof(LocationImportService).GetConstructors(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);

        Assert.Contains(constructors.SelectMany(constructor => constructor.GetParameters()), parameter =>
            parameter.ParameterType == typeof(IDbContextFactory<ApplicationDbContext>));
    }

    [Fact]
    public void Deduplicator_KeyLookupAcceptsOnlyCurrentBatchKeys()
    {
        var filter = typeof(LocationImportDeduplicator).GetMethod(
            "FilterAsync",
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(filter);
        Assert.Contains(filter!.GetParameters(), parameter =>
            parameter.Name == "batchKeys" &&
            parameter.ParameterType == typeof(IReadOnlySet<Guid>));
    }

    [Fact]
    public void ImportWorker_DeclaresBoundedBatchSize()
    {
        var batchSize = typeof(LocationImportService).GetField(
            "BatchSize",
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(batchSize);
        Assert.Equal(50, batchSize!.GetRawConstantValue());
    }

    [Fact]
    public void RestartCursor_IsAppliedByStreamingWorkerRatherThanParserMaterialization()
    {
        var materializingHelper = typeof(LocationImportService).GetMethod(
            "GetLocationsToProcess",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);

        Assert.Null(materializingHelper);
    }
}
