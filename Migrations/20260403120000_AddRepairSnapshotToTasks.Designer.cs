using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ServiceTaskPlanningSystem.Data;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    [DbContext(typeof(ScheduleDbContext))]
    [Migration("20260403120000_AddRepairSnapshotToTasks")]
    partial class AddRepairSnapshotToTasks
    {
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
        }
    }
}
