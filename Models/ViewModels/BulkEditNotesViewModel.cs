// Models/ViewModels/BulkEditNotesViewModel.cs
using Microsoft.AspNetCore.Mvc.Rendering;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models.ViewModels
{
    public class BulkEditNotesViewModel
    {
        // Filters
        [Display(Name = "Country")]
        public string? Country { get; set; }
        
        [Display(Name = "Region")]
        public string? Region { get; set; }
        
        [Display(Name = "Place")]
        public string? Place { get; set; }
        
        [Display(Name = "From Date")]
        [DataType(DataType.Date)]
        public DateTime? FromDate { get; set; }
        
        [Display(Name = "To Date")]
        [DataType(DataType.Date)]
        public DateTime? ToDate { get; set; }

        // Determines whether to append to existing notes or overwrite
        [Display(Name = "Append to Existing Notes?")]
        public bool Append { get; set; }

        // Optional: explicitly clear notes (set to null)
        [Display(Name = "Clear Existing Notes?")]
        public bool ClearNotes { get; set; }

        // The new HTML content from Quill
        public string Notes { get; set; } = string.Empty;

        // Used to populate the dropdowns
        public List<SelectListItem> Countries { get; set; } = new();
        public List<SelectListItem> Regions { get; set; } = new();
        public List<SelectListItem> Places { get; set; } = new();

        // For preview / count of affected rows
        public int? AffectedCount { get; set; }
    }
}