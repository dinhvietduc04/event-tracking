// Historical migration metadata. The current model snapshot is authoritative for design-time tooling.
using EventTracking.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTracking.Persistence.Migrations
{
    [DbContext(typeof(TrackingDbContext))]
    [Migration("20260918122118_ClickHouseProjection")]
    partial class ClickHouseProjection { }
}
