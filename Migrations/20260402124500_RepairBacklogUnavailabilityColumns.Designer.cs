using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ServiceTaskPlanningSystem.Data;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    [DbContext(typeof(ScheduleDbContext))]
    [Migration("20260402124500_RepairBacklogUnavailabilityColumns")]
    partial class RepairBacklogUnavailabilityColumns
    {
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
        }
    }
}
