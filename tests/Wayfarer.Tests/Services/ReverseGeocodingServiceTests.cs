using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text.Json;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests for <see cref="ReverseGeocodingService"/> covering Mapbox reverse geocoding.
/// </summary>
public class ReverseGeocodingServiceTests
{
    #region GetReverseGeocodingDataAsync Tests

    [Fact]
    public async Task GetReverseGeocodingDataAsync_ReturnsEmptyResults_WhenProviderNotSupported()
    {
        // Arrange
        var mockHttpHandler = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(mockHttpHandler.Object);
        var mockLogger = new Mock<ILogger<BaseApiController>>();

        var service = new ReverseGeocodingService(httpClient, mockLogger.Object);

        // Act
        var result = await service.GetReverseGeocodingDataAsync(40.7128, -74.0060, "test-token", "UnsupportedProvider");

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result.Address);
        Assert.Empty(result.FullAddress);
        Assert.Empty(result.Country);
    }

    [Fact]
    public async Task GetReverseGeocodingDataAsync_ReturnsEmptyResults_WhenApiCallFails()
    {
        // Arrange
        var mockHttpHandler = new Mock<HttpMessageHandler>();
        mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var httpClient = new HttpClient(mockHttpHandler.Object);
        var mockLogger = new Mock<ILogger<BaseApiController>>();

        var service = new ReverseGeocodingService(httpClient, mockLogger.Object);

        // Act
        var result = await service.GetReverseGeocodingDataAsync(40.7128, -74.0060, "test-token");

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result.Address);
    }

    [Fact]
    public async Task GetReverseGeocodingDataAsync_ReturnsEmptyResults_WhenNoFeatures()
    {
        // Arrange
        var response = new ReverseLocationResponse
        {
            Type = "FeatureCollection",
            Features = new List<Feature>(),
            Attribution = "Mapbox"
        };

        var mockHttpHandler = CreateMockHttpHandler(HttpStatusCode.OK, response);
        var httpClient = new HttpClient(mockHttpHandler.Object);
        var mockLogger = new Mock<ILogger<BaseApiController>>();

        var service = new ReverseGeocodingService(httpClient, mockLogger.Object);

        // Act
        var result = await service.GetReverseGeocodingDataAsync(40.7128, -74.0060, "test-token");

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result.Address);
    }

    [Fact]
    public async Task GetReverseGeocodingDataAsync_ParsesFullResponse_WithAllFields()
    {
        // Arrange
        var response = CreateFullMapboxResponse();

        var mockHttpHandler = CreateMockHttpHandler(HttpStatusCode.OK, response);
        var httpClient = new HttpClient(mockHttpHandler.Object);
        var mockLogger = new Mock<ILogger<BaseApiController>>();

        var service = new ReverseGeocodingService(httpClient, mockLogger.Object);

        // Act
        var result = await service.GetReverseGeocodingDataAsync(40.7128, -74.0060, "test-token");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Broadway", result.Address);
        Assert.Equal("123 Broadway, New York, NY 10001, USA", result.FullAddress);
        Assert.Equal("123", result.AddressNumber);
        Assert.Equal("Broadway", result.StreetName);
        Assert.Equal("New York", result.Place);
        Assert.Equal("10001", result.PostCode);
        Assert.Equal("New York", result.Region);
        Assert.Equal("United States", result.Country);
    }

    [Fact]
    public async Task GetReverseGeocodingDataAsync_UsesStreetFeature_WhenAvailable()
    {
        // Arrange
        var response = new ReverseLocationResponse
        {
            Type = "FeatureCollection",
            Features = new List<Feature>
            {
                CreateFeature("address", "123 Main St", "123 Main St, City, Country"),
                CreateFeature("street", "Main Street", "Main Street, City, Country")
            }
        };

        var mockHttpHandler = CreateMockHttpHandler(HttpStatusCode.OK, response);
        var httpClient = new HttpClient(mockHttpHandler.Object);
        var mockLogger = new Mock<ILogger<BaseApiController>>();

        var service = new ReverseGeocodingService(httpClient, mockLogger.Object);

        // Act
        var result = await service.GetReverseGeocodingDataAsync(40.7128, -74.0060, "test-token");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Main Street, City, Country", result.FullAddress);
    }

    [Fact]
    public async Task GetReverseGeocodingDataAsync_UsesFirstFeature_WhenNoStreetFeature()
    {
        // Arrange
        var response = new ReverseLocationResponse
        {
            Type = "FeatureCollection",
            Features = new List<Feature>
            {
                CreateFeature("address", "123 Main St", "123 Main St, City, Country"),
                CreateFeature("place", "City Center", "City Center, Country")
            }
        };

        var mockHttpHandler = CreateMockHttpHandler(HttpStatusCode.OK, response);
        var httpClient = new HttpClient(mockHttpHandler.Object);
        var mockLogger = new Mock<ILogger<BaseApiController>>();

        var service = new ReverseGeocodingService(httpClient, mockLogger.Object);

        // Act
        var result = await service.GetReverseGeocodingDataAsync(40.7128, -74.0060, "test-token");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("123 Main St, City, Country", result.FullAddress);
    }

    [Fact]
    public async Task GetReverseGeocodingDataAsync_HandlesNullContext()
    {
        // Arrange
        var response = new ReverseLocationResponse
        {
            Type = "FeatureCollection",
            Features = new List<Feature>
            {
                new Feature
                {
                    Type = "Feature",
                    Id = "test-id",
                    Properties = new FeatureProperties
                    {
                        FeatureType = "address",
                        Name = "Test Location",
                        FullAddress = "Test Full Address",
                        Context = null // No context
                    }
                }
            }
        };

        var mockHttpHandler = CreateMockHttpHandler(HttpStatusCode.OK, response);
        var httpClient = new HttpClient(mockHttpHandler.Object);
        var mockLogger = new Mock<ILogger<BaseApiController>>();

        var service = new ReverseGeocodingService(httpClient, mockLogger.Object);

        // Act
        var result = await service.GetReverseGeocodingDataAsync(40.7128, -74.0060, "test-token");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Test Full Address", result.FullAddress);
        Assert.Null(result.Address);
        Assert.Null(result.Country);
    }

    [Fact]
    public async Task GetReverseGeocodingDataAsync_UsesMapboxProvider_ByDefault()
    {
        // Arrange
        var response = CreateFullMapboxResponse();
        var mockHttpHandler = CreateMockHttpHandler(HttpStatusCode.OK, response);
        var httpClient = new HttpClient(mockHttpHandler.Object);
        var mockLogger = new Mock<ILogger<BaseApiController>>();

        var service = new ReverseGeocodingService(httpClient, mockLogger.Object);

        // Act - not passing provider parameter
        var result = await service.GetReverseGeocodingDataAsync(40.7128, -74.0060, "test-token");

        // Assert - should work with default Mapbox provider
        Assert.NotNull(result);
        Assert.Equal("United States", result.Country);
    }

    [Fact]
    public async Task GetReverseGeocodingDataAsync_ConstructsCorrectUrl()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var response = CreateFullMapboxResponse();

        var mockHttpHandler = new Mock<HttpMessageHandler>();
        mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response))
            });

        var httpClient = new HttpClient(mockHttpHandler.Object);
        var mockLogger = new Mock<ILogger<BaseApiController>>();

        var service = new ReverseGeocodingService(httpClient, mockLogger.Object);

        // Act
        await service.GetReverseGeocodingDataAsync(40.7128, -74.0060, "my-api-token");

        // Assert
        Assert.NotNull(capturedRequest);
        var url = capturedRequest.RequestUri?.ToString();
        Assert.Contains("api.mapbox.com", url);
        Assert.Contains("longitude=-74.006", url);
        Assert.Contains("latitude=40.7128", url);
        Assert.Contains("access_token=my-api-token", url);
    }

    [Fact]
    public async Task GetReverseGeocodingDataAsync_HandlesPartialContext()
    {
        // Arrange
        var response = new ReverseLocationResponse
        {
            Type = "FeatureCollection",
            Features = new List<Feature>
            {
                new Feature
                {
                    Type = "Feature",
                    Id = "test-id",
                    Properties = new FeatureProperties
                    {
                        FeatureType = "street",
                        Name = "Test Street",
                        FullAddress = "Test Street, Country",
                        Context = new Context
                        {
                            Street = new ContextDetail { Name = "Test Street" },
                            Country = new ContextDetail { Name = "Test Country" }
                            // No Place, Region, Postcode, etc.
                        }
                    }
                }
            }
        };

        var mockHttpHandler = CreateMockHttpHandler(HttpStatusCode.OK, response);
        var httpClient = new HttpClient(mockHttpHandler.Object);
        var mockLogger = new Mock<ILogger<BaseApiController>>();

        var service = new ReverseGeocodingService(httpClient, mockLogger.Object);

        // Act
        var result = await service.GetReverseGeocodingDataAsync(40.7128, -74.0060, "test-token");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Test Street", result.Address);
        Assert.Equal("Test Country", result.Country);
        Assert.Null(result.Place);
        Assert.Null(result.Region);
    }

    [Fact]
    public async Task GetReverseGeocodingDataAsync_LogsWarning_WhenUnsupportedProvider()
    {
        // Arrange
        var mockHttpHandler = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(mockHttpHandler.Object);
        var mockLogger = new Mock<ILogger<BaseApiController>>();

        var service = new ReverseGeocodingService(httpClient, mockLogger.Object);

        // Act
        await service.GetReverseGeocodingDataAsync(40.7128, -74.0060, "test-token", "Google");

        // Assert - logger should have been called
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
            Times.Once);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a mock HTTP handler that returns a specific response.
    /// </summary>
    private static Mock<HttpMessageHandler> CreateMockHttpHandler<T>(HttpStatusCode statusCode, T response)
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(JsonSerializer.Serialize(response))
            });

        return mockHandler;
    }

    /// <summary>
    /// Creates a full Mapbox-like response for testing.
    /// </summary>
    private static ReverseLocationResponse CreateFullMapboxResponse()
    {
        return new ReverseLocationResponse
        {
            Type = "FeatureCollection",
            Attribution = "Mapbox",
            Features = new List<Feature>
            {
                new Feature
                {
                    Type = "Feature",
                    Id = "address.123",
                    Geometry = new Geometry
                    {
                        Type = "Point",
                        Coordinates = new List<double> { -74.0060, 40.7128 }
                    },
                    Properties = new FeatureProperties
                    {
                        MapboxId = "dXJuOm1ieGFkcjoxMjM",
                        FeatureType = "street",
                        Name = "Broadway",
                        FullAddress = "123 Broadway, New York, NY 10001, USA",
                        Context = new Context
                        {
                            Address = new ContextAddress
                            {
                                MapboxId = "addr-123",
                                Name = "123 Broadway",
                                AddressNumber = "123",
                                StreetName = "Broadway"
                            },
                            Street = new ContextDetail
                            {
                                MapboxId = "street-123",
                                Name = "Broadway"
                            },
                            Postcode = new ContextDetail
                            {
                                MapboxId = "postcode-10001",
                                Name = "10001"
                            },
                            Place = new ContextDetail
                            {
                                MapboxId = "place-nyc",
                                Name = "New York"
                            },
                            Region = new ContextDetail
                            {
                                MapboxId = "region-ny",
                                Name = "New York"
                            },
                            Country = new ContextDetail
                            {
                                MapboxId = "country-usa",
                                Name = "United States"
                            }
                        }
                    }
                }
            }
        };
    }

    /// <summary>
    /// Creates a simple feature for testing.
    /// </summary>
    private static Feature CreateFeature(string featureType, string name, string fullAddress)
    {
        return new Feature
        {
            Type = "Feature",
            Id = $"{featureType}.{Guid.NewGuid()}",
            Properties = new FeatureProperties
            {
                FeatureType = featureType,
                Name = name,
                FullAddress = fullAddress,
                Context = new Context()
            }
        };
    }

    #endregion
}
