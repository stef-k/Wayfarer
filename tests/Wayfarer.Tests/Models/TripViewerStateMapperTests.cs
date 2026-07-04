using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.TripViewer;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>
/// Verifies the read-only Trip Viewer state mapper and redaction contract.
/// </summary>
public sealed class TripViewerStateMapperTests : TestBase
{
    [Fact]
    public void ToPrivateState_ReturnsNormalizedStateWithOwnerData()
    {
        var fixture = BuildTripFixture(isPublic: true, shareProgress: true);

        var state = TripViewerStateMapper.ToPrivateState(fixture.Trip, fixture.Visits, Query(lat: "44.1", lon: "23.2", lng: "99", zoom: "11"));

        Assert.Equal("private", state.ViewerMode);
        Assert.True(state.Permissions.CanViewPrivateState);
        Assert.True(state.Actions.Edit.Allowed);
        Assert.True(state.Actions.ExportWayfarerKml.Allowed);
        Assert.Equal("query", state.Map.InitialView.Source);
        Assert.Equal(23.2, state.Map.InitialView.Longitude);
        Assert.Equal("lat=44.1&lon=23.2&zoom=11", state.Map.InitialView.CanonicalQuery);
        Assert.Single(state.RegionsById);
        Assert.Equal(fixture.Region.Id, state.RegionOrder.Single());
        Assert.Equal(fixture.Place.Id, state.PlaceOrderByRegionId[fixture.Region.Id].Single());
        Assert.Equal(fixture.Area.Id, state.AreaOrderByRegionId[fixture.Region.Id].Single());
        Assert.Equal(fixture.Segment.Id, state.SegmentOrder.Single());
        Assert.Equal("hiking", state.TagOrder.Single());
        Assert.Equal(1, state.VisitProgress.VisitedPlaces);
        Assert.True(state.VisitProgress.CanDisplayHistory);
        Assert.NotEmpty(state.VisitProgress.HistoryRows);
        Assert.NotNull(state.VisitProgress.PlaceSummariesByPlaceId[fixture.Place.Id].FirstVisitAt);
        Assert.Equal(45, state.VisitProgress.HistoryRows.Single().DurationMinutes);
        Assert.Equal($"/User/Trip/ViewNext/{fixture.Trip.Id}", state.Trip.PrivateUrl);
    }

    [Fact]
    public void ToPublicState_ForAuthenticatedOwner_RemainsPublicWithExplicitOwnerAllowance()
    {
        var fixture = BuildTripFixture(isPublic: true, shareProgress: false);

        var state = TripViewerStateMapper.ToPublicState(fixture.Trip, fixture.Visits, isOwner: true, isAuthenticated: true, embed: false, new QueryCollection());

        Assert.Equal("public", state.ViewerMode);
        Assert.False(state.Permissions.CanViewPrivateState);
        Assert.True(state.Permissions.IsOwner);
        Assert.True(state.Actions.Edit.Allowed);
        Assert.True(state.VisitProgress.CanDisplayHistory);
        Assert.NotNull(state.VisitProgress.PlaceSummariesByPlaceId[fixture.Place.Id].LastVisitAt);
        Assert.Null(state.Trip.CoverImage!.RawUrl);
    }

    [Fact]
    public void ToPublicState_ForAuthenticatedOwnerEmbed_RemainsEmbedAndRedactsOwnerOnlyData()
    {
        var fixture = BuildTripFixture(isPublic: true, shareProgress: true);

        var state = TripViewerStateMapper.ToPublicState(fixture.Trip, fixture.Visits, isOwner: true, isAuthenticated: true, embed: true, new QueryCollection());

        Assert.Equal("embed", state.ViewerMode);
        Assert.False(state.Permissions.IsOwner);
        Assert.False(state.Permissions.CanReadVisitHistory);
        Assert.False(state.Actions.Edit.Allowed);
        Assert.False(state.Actions.Clone.Allowed);
        Assert.False(state.Actions.ExportPdf.Allowed);
        Assert.False(state.Actions.CopyCoverUrl.Allowed);
        Assert.True(state.Actions.OpenCanonical.Allowed);
        Assert.Null(state.Trip.PrivateUrl);
        Assert.Empty(state.VisitProgress.HistoryRows);
        Assert.Null(state.VisitProgress.PlaceSummariesByPlaceId[fixture.Place.Id].FirstVisitAt);
        Assert.Null(state.Trip.CoverImage!.RawUrl);
    }

    [Fact]
    public void ToPublicState_RedactsNonOwnerProgressWhenSharingDisabled()
    {
        var fixture = BuildTripFixture(isPublic: true, shareProgress: false);

        var state = TripViewerStateMapper.ToPublicState(fixture.Trip, fixture.Visits, isOwner: false, isAuthenticated: false, embed: false, new QueryCollection());

        Assert.False(state.VisitProgress.CanDisplayCounts);
        Assert.Equal(0, state.VisitProgress.VisitedPlaces);
        Assert.Equal(0, state.VisitProgress.PlaceSummariesByPlaceId[fixture.Place.Id].VisitCount);
        Assert.Empty(state.VisitProgress.HistoryRows);
        Assert.False(state.Actions.Clone.Allowed);
        Assert.True(state.Actions.Clone.RequiresAuthentication);
        Assert.Contains("/Identity/Account/Login", state.Actions.Clone.Url);
        Assert.Null(state.Trip.PrivateUrl);
    }

    [Theory]
    [InlineData("""<a href="https://example.test">safe</a>""", "href=\"https://example.test\"", true)]
    [InlineData("""<a href="http://example.test">safe</a>""", "href=\"http://example.test\"", true)]
    [InlineData("""<a href="javascript:alert(1)">unsafe</a>""", "href=", false)]
    [InlineData("""<a href="data:text/html,unsafe">unsafe</a>""", "href=", false)]
    [InlineData("""<a href="vbscript:msgbox(1)">unsafe</a>""", "href=", false)]
    [InlineData("""<a href="ftp://example.test/file">unsafe</a>""", "href=", false)]
    [InlineData("""<a href="mailto:test@example.test">unsafe</a>""", "href=", false)]
    [InlineData("""<a href="/relative">unsafe</a>""", "href=", false)]
    [InlineData("""<a href="//example.test/path">unsafe</a>""", "href=", false)]
    [InlineData("<a href=\"java\u0000script:alert(1)\">unsafe</a>", "href=", false)]
    public void NotesPayload_AllowsOnlyAbsoluteHttpLinks(string html, string expected, bool shouldPreserveSafeAttributes)
    {
        var fixture = BuildTripFixture(isPublic: true, shareProgress: true);
        fixture.Trip.Notes = html;

        var state = TripViewerStateMapper.ToPublicState(fixture.Trip, Array.Empty<PlaceVisitEvent>(), isOwner: false, isAuthenticated: false, embed: false, new QueryCollection());

        if (shouldPreserveSafeAttributes)
        {
            Assert.Contains(expected, state.Trip.Notes.DisplayHtml);
            Assert.Contains("rel=\"noopener noreferrer\"", state.Trip.Notes.DisplayHtml);
            Assert.Contains("target=\"_blank\"", state.Trip.Notes.DisplayHtml);
        }
        else
        {
            Assert.DoesNotContain(expected, state.Trip.Notes.DisplayHtml);
            Assert.DoesNotContain("target=\"_blank\"", state.Trip.Notes.DisplayHtml);
        }
    }

    [Fact]
    public void ToPublicState_AllowsAuthenticatedNonOwnerCloneWithoutPrivateActions()
    {
        var fixture = BuildTripFixture(isPublic: true, shareProgress: true);

        var state = TripViewerStateMapper.ToPublicState(fixture.Trip, fixture.Visits, isOwner: false, isAuthenticated: true, embed: false, new QueryCollection());

        Assert.True(state.Actions.Clone.Allowed);
        Assert.Equal("POST", state.Actions.Clone.Method);
        Assert.False(state.Actions.Edit.Allowed);
        Assert.True(state.VisitProgress.CanDisplayCounts);
        Assert.False(state.VisitProgress.CanDisplayHistory);
        Assert.Equal(1, state.VisitProgress.PlaceSummariesByPlaceId[fixture.Place.Id].VisitCount);
        Assert.Null(state.VisitProgress.PlaceSummariesByPlaceId[fixture.Place.Id].FirstVisitAt);
    }

    [Fact]
    public void NotesPayload_SanitizesHtmlAndPreservesProxyImages()
    {
        var fixture = BuildTripFixture(isPublic: true, shareProgress: true);
        fixture.Trip.Notes = """
            <script>alert(1)</script><p onclick="evil()">Safe <strong>text</strong></p>
            <a href="javascript:alert(1)">bad</a>
            <img src="data:image/png;base64,abc" onerror="evil()">
            <p><img src="https://cdn.example.test/photo.jpg"></p>
            """;

        var state = TripViewerStateMapper.ToPublicState(fixture.Trip, Array.Empty<PlaceVisitEvent>(), isOwner: false, isAuthenticated: false, embed: false, new QueryCollection());
        var notes = state.Trip.Notes;

        Assert.DoesNotContain("script", notes.DisplayHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", notes.DisplayHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", notes.DisplayHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:image", notes.DisplayHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/Public/ProxyImage?url=https%3A%2F%2Fcdn.example.test%2Fphoto.jpg", notes.DisplayHtml);
        Assert.True(notes.HasRenderableContent);
        Assert.True(notes.HasTextContent);
        Assert.True(notes.HasMediaContent);
        Assert.Equal("Safe text bad", notes.PlainText);
    }

    [Fact]
    public void NotesPayload_TreatsImageOnlyAsRenderableAndDoesNotParseMarkdown()
    {
        var fixture = BuildTripFixture(isPublic: true, shareProgress: true);
        fixture.Trip.Notes = """<p><img src="/Public/ProxyImage?url=https%3A%2F%2Fcdn.example.test%2Fphoto.jpg"></p>""";
        fixture.Region.Notes = "## Trail notes **bold** [Guide](https://example.test/guide)";

        var state = TripViewerStateMapper.ToPublicState(fixture.Trip, Array.Empty<PlaceVisitEvent>(), isOwner: false, isAuthenticated: false, embed: false, new QueryCollection());

        Assert.True(state.Trip.Notes.HasRenderableContent);
        Assert.True(state.Trip.Notes.HasMediaContent);
        Assert.False(state.Trip.Notes.HasTextContent);
        Assert.Contains("/Public/ProxyImage?url=https%3A%2F%2Fcdn.example.test%2Fphoto.jpg", state.Trip.Notes.DisplayHtml);
        Assert.Contains("## Trail notes **bold** [Guide](https://example.test/guide)", state.RegionsById[fixture.Region.Id].Notes.PlainText);
        Assert.DoesNotContain("<h2>", state.RegionsById[fixture.Region.Id].Notes.DisplayHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeometryAndCoordinates_UseDeterministicConventions()
    {
        var fixture = BuildTripFixture(isPublic: true, shareProgress: true);

        var state = TripViewerStateMapper.ToPrivateState(fixture.Trip, Array.Empty<PlaceVisitEvent>(), new QueryCollection());

        Assert.Equal(48.5, state.PlacesById[fixture.Place.Id].Location!.Latitude);
        Assert.Equal(2.2, state.PlacesById[fixture.Place.Id].Location!.Longitude);
        Assert.Equal(48.0, state.RegionsById[fixture.Region.Id].Center!.Latitude);
        Assert.Equal(2.0, state.RegionsById[fixture.Region.Id].Center!.Longitude);
        var areaCoordinates = state.AreasById[fixture.Area.Id].Geometry!.Value
            .GetProperty("coordinates")[0][0];
        Assert.Equal(2.0, areaCoordinates[0].GetDouble());
        Assert.Equal(48.0, areaCoordinates[1].GetDouble());
        var routeCoordinates = state.SegmentsById[fixture.Segment.Id].Route!.Value
            .GetProperty("coordinates")[0];
        Assert.Equal(2.2, routeCoordinates[0].GetDouble());
        Assert.Equal(48.5, routeCoordinates[1].GetDouble());
    }

    [Fact]
    public void MapQuery_AcceptsLngAliasWhenLonIsAbsent()
    {
        var fixture = BuildTripFixture(isPublic: true, shareProgress: true);

        var state = TripViewerStateMapper.ToPrivateState(fixture.Trip, Array.Empty<PlaceVisitEvent>(), Query(lat: "1.5", lng: "2.5", zoom: "6"));

        Assert.Equal("query", state.Map.InitialView.Source);
        Assert.Equal(2.5, state.Map.InitialView.Longitude);
        Assert.Equal("lat=1.5&lon=2.5&zoom=6", state.Map.InitialView.CanonicalQuery);
        Assert.Contains("lng", state.Map.AcceptedQueryParameters);
        Assert.DoesNotContain("lng", state.Map.EmittedQueryParameters);
    }

    [Fact]
    public void Places_AllowValidViewerMarkerValues()
    {
        var fixture = BuildTripFixture(isPublic: true, shareProgress: true);
        fixture.Place.IconName = "museum";
        fixture.Place.MarkerColor = "bg-green";

        var state = TripViewerStateMapper.ToPublicState(fixture.Trip, Array.Empty<PlaceVisitEvent>(), isOwner: false, isAuthenticated: false, embed: false, new QueryCollection());

        Assert.Equal("museum", state.PlacesById[fixture.Place.Id].IconName);
        Assert.Equal("bg-green", state.PlacesById[fixture.Place.Id].MarkerColor);
    }

    [Fact]
    public void Places_FallbackInvalidViewerMarkerValues()
    {
        var fixture = BuildTripFixture(isPublic: true, shareProgress: true);
        fixture.Place.IconName = "../private";
        fixture.Place.MarkerColor = "bg-unknown";

        var state = TripViewerStateMapper.ToPublicState(fixture.Trip, Array.Empty<PlaceVisitEvent>(), isOwner: false, isAuthenticated: false, embed: false, new QueryCollection());

        Assert.Equal("marker", state.PlacesById[fixture.Place.Id].IconName);
        Assert.Equal("bg-blue", state.PlacesById[fixture.Place.Id].MarkerColor);
    }

    private static QueryCollection Query(string? lat = null, string? lon = null, string? lng = null, string? zoom = null)
    {
        var values = new Dictionary<string, StringValues>();
        if (lat != null) values["lat"] = lat;
        if (lon != null) values["lon"] = lon;
        if (lng != null) values["lng"] = lng;
        if (zoom != null) values["zoom"] = zoom;
        return new QueryCollection(values);
    }

    private static TripFixture BuildTripFixture(bool isPublic, bool shareProgress)
    {
        var owner = TestDataFixtures.CreateUser(id: "owner", displayName: "Owner Name");
        var trip = TestDataFixtures.CreateTrip(owner, "Viewer Trip", isPublic);
        trip.ShareProgressEnabled = shareProgress;
        trip.Notes = "<p>Trip notes</p>";
        trip.CenterLat = 47;
        trip.CenterLon = 3;
        trip.Zoom = 7;
        trip.CoverImageUrl = "https://cdn.example.test/cover.jpg";
        trip.Tags = new List<Tag> { new() { Id = Guid.NewGuid(), Name = "Hiking", Slug = "hiking" } };

        var region = TestDataFixtures.CreateRegion(trip, "Region", displayOrder: 1);
        region.Notes = "<p>Region notes</p>";
        region.Center = new Point(2.0, 48.0) { SRID = 4326 };
        region.CoverImageUrl = "https://cdn.example.test/region.jpg";

        var place = TestDataFixtures.CreatePlace(region, "Place", latitude: 48.5, longitude: 2.2, displayOrder: 1);
        place.Notes = "<p>Place notes</p>";
        place.Address = "1 Main";
        place.IconName = "museum";
        place.MarkerColor = "bg-green";

        var area = new Area
        {
            Id = Guid.NewGuid(),
            RegionId = region.Id,
            Region = region,
            Name = "Area",
            Notes = "<p>Area notes</p>",
            DisplayOrder = 1,
            FillHex = "#00ff00",
            Geometry = new Polygon(new LinearRing(new[]
            {
                new Coordinate(2.0, 48.0),
                new Coordinate(2.1, 48.0),
                new Coordinate(2.1, 48.1),
                new Coordinate(2.0, 48.1),
                new Coordinate(2.0, 48.0)
            })) { SRID = 4326 }
        };

        var segment = new Segment
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            Trip = trip,
            UserId = owner.Id,
            FromPlaceId = place.Id,
            ToPlaceId = place.Id,
            Mode = "walk",
            EstimatedDistanceKm = 3.5,
            EstimatedDuration = TimeSpan.FromMinutes(50),
            DisplayOrder = 1,
            Notes = "<p>Segment notes</p>",
            RouteGeometry = new LineString(new[]
            {
                new Coordinate(2.2, 48.5),
                new Coordinate(2.3, 48.6)
            }) { SRID = 4326 }
        };

        region.Places = new List<Place> { place };
        region.Areas = new List<Area> { area };
        trip.Regions = new List<Region> { region };
        trip.Segments = new List<Segment> { segment };

        var visit = new PlaceVisitEvent
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            PlaceId = place.Id,
            ArrivedAtUtc = new DateTime(2026, 1, 2, 10, 0, 0, DateTimeKind.Utc),
            LastSeenAtUtc = new DateTime(2026, 1, 2, 10, 45, 0, DateTimeKind.Utc),
            EndedAtUtc = new DateTime(2026, 1, 2, 10, 45, 0, DateTimeKind.Utc)
        };

        return new TripFixture(trip, region, place, area, segment, new[] { visit });
    }

    private sealed record TripFixture(
        Trip Trip,
        Region Region,
        Place Place,
        Area Area,
        Segment Segment,
        IReadOnlyList<PlaceVisitEvent> Visits);
}
