using System;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Authorization;
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using Xunit;

namespace Tests.Unit.Authorization;

public class HttpUserContextResolverTests
{
    [Fact]
    public void NoPrincipal_ReturnsAnonymousFallback()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);
        var logger = Substitute.For<ILogger<HttpUserContextResolver>>();
        using var db = CreateContext();

        var resolver = new HttpUserContextResolver(accessor, db, logger);
        var user = resolver.GetCurrentUser();

        AssertAnonymousFallback(user);
    }

    [Fact]
    public void MissingNameIdentifier_ReturnsAnonymousFallback()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "manager") }, authenticationType: "test", nameType: null, roleType: ClaimTypes.Role));
        var accessor = CreateAccessor(principal);
        var logger = Substitute.For<ILogger<HttpUserContextResolver>>();
        using var db = CreateContext();

        var resolver = new HttpUserContextResolver(accessor, db, logger);
        var user = resolver.GetCurrentUser();

        AssertAnonymousFallback(user);
    }

    [Fact]
    public void UnknownUser_ReturnsAnonymousFallback()
    {
        var principal = CreatePrincipal("ghost@test.com", "manager");
        var accessor = CreateAccessor(principal);
        var logger = Substitute.For<ILogger<HttpUserContextResolver>>();
        using var db = CreateContext();

        var resolver = new HttpUserContextResolver(accessor, db, logger);
        var user = resolver.GetCurrentUser();

        AssertAnonymousFallback(user);
    }

    [Fact]
    public void InactiveUser_ReturnsAnonymousFallback()
    {
        const string email = "inactive@test.com";
        var principal = CreatePrincipal(email, "manager");
        var accessor = CreateAccessor(principal);
        var logger = Substitute.For<ILogger<HttpUserContextResolver>>();
        using var db = CreateContext();
        db.Users.Add(new UserEntity
        {
            Id = 1,
            Email = email,
            Name = "Inactive",
            Role = UserRole.Manager,
            IsActive = false
        });
        db.SaveChanges();

        var resolver = new HttpUserContextResolver(accessor, db, logger);
        var user = resolver.GetCurrentUser();

        AssertAnonymousFallback(user);
    }

    [Fact]
    public void ManagerUser_ReturnsManagerContext()
    {
        const string email = "manager@test.com";
        var principal = CreatePrincipal(email, "manager");
        var accessor = CreateAccessor(principal);
        var logger = Substitute.For<ILogger<HttpUserContextResolver>>();
        using var db = CreateContext();
        db.Users.Add(new UserEntity
        {
            Id = 10,
            Email = email,
            Name = "Manager",
            Role = UserRole.Manager,
            IsActive = true
        });
        db.SaveChanges();

        var resolver = new HttpUserContextResolver(accessor, db, logger);
        var user = resolver.GetCurrentUser();

        user.Role.Should().Be(UserRole.Manager);
        user.UserId.Should().Be(10);
        user.TechnicianId.Should().BeNull();
    }

    [Fact]
    public void TechnicianWithLinkedRow_ReturnsTechnicianAndMapping()
    {
        const string email = "tech@test.com";
        var principal = CreatePrincipal(email, "technician");
        var accessor = CreateAccessor(principal);
        var logger = Substitute.For<ILogger<HttpUserContextResolver>>();
        using var db = CreateContext();
        SeedUserAndTechnician(db, email, technicianId: 200, userId: 20);

        var resolver = new HttpUserContextResolver(accessor, db, logger);
        var user = resolver.GetCurrentUser();

        user.Role.Should().Be(UserRole.Technician);
        user.UserId.Should().Be(20);
        user.TechnicianId.Should().Be(200);
    }

    [Fact]
    public void TechnicianWithoutTechnicianRow_ReturnsNullTechnicianId()
    {
        const string email = "tech-no-row@test.com";
        var principal = CreatePrincipal(email, "technician");
        var accessor = CreateAccessor(principal);
        var logger = Substitute.For<ILogger<HttpUserContextResolver>>();
        using var db = CreateContext();
        db.Users.Add(new UserEntity
        {
            Id = 30,
            Email = email,
            Name = "TechNoRow",
            Role = UserRole.Technician,
            IsActive = true
        });
        db.SaveChanges();

        var resolver = new HttpUserContextResolver(accessor, db, logger);
        var user = resolver.GetCurrentUser();

        user.Role.Should().Be(UserRole.Technician);
        user.UserId.Should().Be(30);
        user.TechnicianId.Should().BeNull();
    }

    [Fact]
    public void TechnicianWithInactiveUser_ReturnsAnonymousFallback()
    {
        const string email = "tech-inactive@test.com";
        var principal = CreatePrincipal(email, "technician");
        var accessor = CreateAccessor(principal);
        var logger = Substitute.For<ILogger<HttpUserContextResolver>>();
        using var db = CreateContext();
        SeedUserAndTechnician(db, email, technicianId: 300, userId: 40, isActive: false);

        var resolver = new HttpUserContextResolver(accessor, db, logger);
        var user = resolver.GetCurrentUser();

        AssertAnonymousFallback(user);
    }

    private static IHttpContextAccessor CreateAccessor(ClaimsPrincipal principal)
    {
        var context = new DefaultHttpContext { User = principal };
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);
        return accessor;
    }

    private static ClaimsPrincipal CreatePrincipal(string email, string role)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, email),
            new Claim(ClaimTypes.Role, role)
        }, authenticationType: "test", nameType: null, roleType: ClaimTypes.Role);

        return new ClaimsPrincipal(identity);
    }

    private static ScheduleDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ScheduleDbContext(options);
    }

    private static void SeedUserAndTechnician(ScheduleDbContext db, string email, long technicianId, long userId, bool isActive = true)
    {
        db.Users.Add(new UserEntity
        {
            Id = userId,
            Email = email,
            Name = "User",
            Role = UserRole.Technician,
            IsActive = isActive
        });
        db.Regions.Add(new RegionEntity { Id = 1, Name = "Region", Country = "LT" });
        db.Addresses.Add(new AddressEntity
        {
            Id = 1,
            RegionId = 1,
            Street = "Addr",
            FullAddress = "Addr"
        });
        db.Technicians.Add(new TechnicianEntity
        {
            Id = technicianId,
            UserId = userId,
            RegionId = 1,
            WorkplaceAddressId = 1,
            DailyLimit = 8,
            WeeklyLimit = 40,
            TravelSwitchLimit = 0
        });
        db.SaveChanges();
    }

    private static void AssertAnonymousFallback(UserContext user)
    {
        user.Role.Should().Be(UserRole.Anonymous);
        user.UserId.Should().BeNull();
        user.TechnicianId.Should().BeNull();
    }
}
