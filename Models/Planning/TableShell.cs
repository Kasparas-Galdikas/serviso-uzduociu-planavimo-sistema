using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Models.Planning
{
    public class TableShellModel
    {
        public string FirstColumnTitle { get; set; } = "Technicians";
        public string? FirstColumnWidth { get; set; } = "180px";
        public string TableCssClass { get; set; } = "schedule-table";
        public IEnumerable<TableColumn> Columns { get; set; } = new List<TableColumn>();
        public IEnumerable<TableRow> Rows { get; set; } = new List<TableRow>();
    }

    public class TableColumn
    {
        public string Title { get; set; } = string.Empty;
        public string? Width { get; set; }
        public string? CssClass { get; set; }
    }

    public class TableRow
    {
        /// <summary>
        /// Structured data for technician header. When set, partials use this instead of HeaderHtml.
        /// </summary>
        public TechnicianHeaderInfo? TechnicianInfo { get; set; }

        /// <summary>
        /// Legacy HTML content for header. Used only when TechnicianInfo is null.
        /// </summary>
        public string HeaderHtml { get; set; } = string.Empty;

        public IEnumerable<TableCell> Cells { get; set; } = new List<TableCell>();
    }

    public class TableCell
    {
        /// <summary>
        /// Legacy HTML content. Used only when DailyData and WeeklyData are null.
        /// </summary>
        public string ContentHtml { get; set; } = string.Empty;

        public string CssClass { get; set; } = "schedule-cell";
        public int ColSpan { get; set; } = 1;

        /// <summary>
        /// Structured data for daily schedule cells. When set, partials render this.
        /// </summary>
        public DailyCellData? DailyData { get; set; }

        /// <summary>
        /// Structured data for weekly summary cells. When set, partials render this.
        /// </summary>
        public WeeklyCellData? WeeklyData { get; set; }
    }
}
