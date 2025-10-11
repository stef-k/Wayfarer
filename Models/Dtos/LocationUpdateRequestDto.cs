using System;

namespace Wayfarer.Models.Dtos
{
    /// <summary>
    /// Request body for updating a Location (timeline) entry.
    /// All properties are optional; only provided values are applied.
    /// Supports explicit clearing via boolean flags for notes and activity.
    /// </summary>
    public class LocationUpdateRequestDto
    {
        /// <summary>
        /// New latitude in degrees. Must be provided together with <see cref="Longitude"/> if present.
        /// </summary>
        public double? Latitude { get; set; }

        /// <summary>
        /// New longitude in degrees. Must be provided together with <see cref="Latitude"/> if present.
        /// </summary>
        public double? Longitude { get; set; }

        /// <summary>
        /// Optional new notes value. Set <see cref="ClearNotes"/> to true to clear existing notes.
        /// </summary>
        public string? Notes { get; set; }

        /// <summary>
        /// Optional new local timestamp. If not UTC, it will be converted to UTC using the provided
        /// coordinates (or the existing location coordinates when coordinates are not provided).
        /// </summary>
        public DateTime? LocalTimestamp { get; set; }

        /// <summary>
        /// Optional new activity type ID. If not found, server will try to resolve by <see cref="ActivityName"/>.
        /// If neither resolves, activity will be set to null if an activity update was requested.
        /// </summary>
        public int? ActivityTypeId { get; set; }

        /// <summary>
        /// Optional activity name for resolving activity when <see cref="ActivityTypeId"/> is not valid.
        /// </summary>
        public string? ActivityName { get; set; }

        /// <summary>
        /// When true, clears notes (sets to null) regardless of the value of <see cref="Notes"/>.
        /// </summary>
        public bool? ClearNotes { get; set; }

        /// <summary>
        /// When true, clears activity (sets ActivityTypeId to null) regardless of other activity fields.
        /// </summary>
        public bool? ClearActivity { get; set; }
    }
}

