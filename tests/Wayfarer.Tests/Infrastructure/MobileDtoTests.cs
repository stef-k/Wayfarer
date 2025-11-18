using System;
using System.Text.Json;
using Wayfarer.Models.Dtos;
using Xunit;

namespace Wayfarer.Tests.Infrastructure;

public class MobileDtoTests
{
    [Fact]
    public void MobileLocationSseEventDto_SerializesLegacyAndMobileFields()
    {
        var dto = new MobileLocationSseEventDto
        {
            LocationId = 42,
            TimeStamp = DateTime.UtcNow,
            UserId = "user-1",
            UserName = "sample",
            IsLive = true,
            Type = "check-in"
        };

        var json = JsonSerializer.Serialize(dto);

        Assert.Contains("\"LocationId\":42", json);
        Assert.Contains("\"locationId\":42", json);
        Assert.Contains("\"TimeStamp\"", json);
        Assert.Contains("\"timestampUtc\"", json);
        Assert.Contains("\"userId\":\"user-1\"", json);
        Assert.Contains("\"userName\":\"sample\"", json);
        Assert.Contains("\"isLive\":true", json);
        Assert.Contains("\"Type\":\"check-in\"", json);
    }

    [Fact]
    public void GroupLocationsQueryResponse_ReportsMetadata()
    {
        var response = new GroupLocationsQueryResponse
        {
            TotalItems = 10,
            ReturnedItems = 3,
            PageSize = 3,
            HasMore = true,
            NextPageToken = "token",
            IsTruncated = true,
            Results = Array.Empty<PublicLocationDto>()
        };

        Assert.Equal(10, response.TotalItems);
        Assert.Equal(3, response.ReturnedItems);
        Assert.True(response.HasMore);
        Assert.Equal("token", response.NextPageToken);
        Assert.True(response.IsTruncated);
    }
}
