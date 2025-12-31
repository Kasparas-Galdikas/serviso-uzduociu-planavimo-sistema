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
        public string HeaderHtml { get; set; } = string.Empty;
        public IEnumerable<TableCell> Cells { get; set; } = new List<TableCell>();
    }

    public class TableCell
    {
        public string ContentHtml { get; set; } = string.Empty;
        public string CssClass { get; set; } = "schedule-cell";
        public int ColSpan { get; set; } = 1;
    }
}
