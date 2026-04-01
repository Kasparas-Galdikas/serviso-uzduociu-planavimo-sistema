using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.Planning;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared
{
    internal class ServiceLocationResolver
    {
        internal sealed record ServiceLocationLookupCandidate(string? ObjectName, string? ObjectAddress);

        internal sealed class ServiceAddressLookupCache
        {
            public Dictionary<string, long> AddressIdByAddress { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<(string Name, long AddressId), bool> ServiceObjectExists { get; } = new();
        }

        private readonly ScheduleDbContext _db;

        public ServiceLocationResolver(ScheduleDbContext db)
        {
            _db = db;
        }

        internal async Task<long> FindOrCreateServiceObjectAsync(string? objectName, string? objectAddress, string city, CancellationToken cancellationToken = default)
        {
            var normalizedName = string.IsNullOrWhiteSpace(objectName) ? null : objectName.Trim();
            var normalizedAddress = string.IsNullOrWhiteSpace(objectAddress) ? null : objectAddress.Trim();

            // If both name and address are empty, reject.
            if (string.IsNullOrEmpty(normalizedName) && string.IsNullOrEmpty(normalizedAddress))
            {
                throw new InvalidOperationException("Object name and address are required.");
            }

            // Use default values if one is missing
            if (string.IsNullOrEmpty(normalizedName))
            {
                normalizedName = "Objektas";
            }
            if (string.IsNullOrEmpty(normalizedAddress))
            {
                normalizedAddress = "Nežinomas adresas";
            }
            var normalizedCity = string.IsNullOrWhiteSpace(city) ? ParseCity(normalizedAddress) : city.Trim();
            if (string.IsNullOrWhiteSpace(normalizedCity))
            {
                normalizedCity = "Nezinomas";
            }

            var street = ParseStreet(normalizedAddress);
            // Prefer exact full-address reuse globally to avoid duplicate address rows
            // when city inference differs between operations.
            var existingAddress = await _db.Addresses
                .FirstOrDefaultAsync(a => a.FullAddress == normalizedAddress, cancellationToken);

            if (existingAddress == null)
            {
                existingAddress = await _db.Addresses
                    .FirstOrDefaultAsync(a =>
                        a.City == normalizedCity &&
                        a.Street == street,
                        cancellationToken);
            }

            if (existingAddress == null)
            {
                existingAddress = new AddressEntity
                {
                    City = normalizedCity,
                    Street = street,
                    FullAddress = normalizedAddress,
                    CreatedAt = DateTime.UtcNow
                };
                _db.Addresses.Add(existingAddress);
            await _db.SaveChangesAsync(cancellationToken);
            }

            // Try to find existing service object by name and address
            var existingObject = await _db.ServiceObjects
                .FirstOrDefaultAsync(o => o.Name == normalizedName && o.AddressId == existingAddress.Id, cancellationToken);

            if (existingObject != null)
            {
                return existingObject.Id;
            }

            // Create new service object
            var newObject = new ServiceObjectEntity
            {
                Name = normalizedName,
                AddressId = existingAddress.Id,
                CreatedAt = DateTime.UtcNow
            };

            _db.ServiceObjects.Add(newObject);
            await _db.SaveChangesAsync(cancellationToken);

            return newObject.Id;
        }

        internal static string ParseStreet(string fullAddress)
        {
            if (string.IsNullOrWhiteSpace(fullAddress))
            {
                return string.Empty;
            }

            var firstPart = fullAddress.Split(',', 2, StringSplitOptions.TrimEntries)[0];
            if (string.IsNullOrWhiteSpace(firstPart))
            {
                return fullAddress.Trim();
            }

            return firstPart.Trim();
        }

        internal static string ParseCity(string fullAddress)
        {
            if (string.IsNullOrWhiteSpace(fullAddress))
            {
                return string.Empty;
            }

            var parts = fullAddress.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? parts[^1] : string.Empty;
        }

        internal async Task<long?> TryLookupServiceAddressIdAsync(
            string? objectName,
            string? objectAddress,
            CancellationToken cancellationToken,
            ServiceAddressLookupCache? cache = null)
        {
            var normalizedAddress = objectAddress?.Trim();
            var normalizedName = objectName?.Trim();
            if (string.IsNullOrEmpty(normalizedAddress)) return null;

            long addressId;
            if (cache != null && cache.AddressIdByAddress.TryGetValue(normalizedAddress, out var cachedAddressId))
            {
                addressId = cachedAddressId;
            }
            else
            {
                var existingAddress = await _db.Addresses.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.FullAddress == normalizedAddress, cancellationToken);
                if (existingAddress == null) return null;

                addressId = existingAddress.Id;
                cache?.AddressIdByAddress.TryAdd(normalizedAddress, addressId);
            }

            if (string.IsNullOrEmpty(normalizedName)) return addressId;

            if (cache != null && cache.ServiceObjectExists.TryGetValue((normalizedName, addressId), out var cachedExists))
            {
                return cachedExists ? addressId : null;
            }

            var objectExists = await _db.ServiceObjects.AsNoTracking()
                .AnyAsync(o => o.Name == normalizedName && o.AddressId == addressId, cancellationToken);

            cache?.ServiceObjectExists.TryAdd((normalizedName, addressId), objectExists);

            return objectExists ? addressId : null;
        }

        internal async Task<ServiceAddressLookupCache> BuildLookupCacheAsync(
            IEnumerable<ServiceLocationLookupCandidate> candidates,
            CancellationToken cancellationToken)
        {
            var cache = new ServiceAddressLookupCache();
            var normalizedCandidates = candidates
                .Select(candidate => new
                {
                    ObjectName = candidate.ObjectName?.Trim(),
                    ObjectAddress = candidate.ObjectAddress?.Trim()
                })
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ObjectAddress))
                .ToList();

            if (normalizedCandidates.Count == 0)
            {
                return cache;
            }

            var addresses = normalizedCandidates
                .Select(candidate => candidate.ObjectAddress!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var existingAddresses = await _db.Addresses
                .AsNoTracking()
                .Where(address => addresses.Contains(address.FullAddress))
                .Select(address => new { address.Id, address.FullAddress })
                .ToListAsync(cancellationToken);

            foreach (var existingAddress in existingAddresses)
            {
                cache.AddressIdByAddress[existingAddress.FullAddress] = existingAddress.Id;
            }

            var requestedPairs = normalizedCandidates
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ObjectName))
                .Select(candidate =>
                {
                    var addressId = cache.AddressIdByAddress.GetValueOrDefault(candidate.ObjectAddress!);
                    return addressId > 0
                        ? (Name: candidate.ObjectName!, AddressId: addressId)
                        : ((string Name, long AddressId)?)null;
                })
                .Where(pair => pair.HasValue)
                .Select(pair => pair!.Value)
                .Distinct()
                .ToList();

            if (requestedPairs.Count == 0)
            {
                return cache;
            }

            foreach (var requestedPair in requestedPairs)
            {
                cache.ServiceObjectExists[requestedPair] = false;
            }

            var requestedAddressIds = requestedPairs
                .Select(pair => pair.AddressId)
                .Distinct()
                .ToList();

            var requestedNames = requestedPairs
                .Select(pair => pair.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var existingObjects = await _db.ServiceObjects
                .AsNoTracking()
                .Where(serviceObject => requestedAddressIds.Contains(serviceObject.AddressId))
                .Where(serviceObject => requestedNames.Contains(serviceObject.Name))
                .Select(serviceObject => new { serviceObject.Name, serviceObject.AddressId })
                .ToListAsync(cancellationToken);

            foreach (var existingObject in existingObjects)
            {
                cache.ServiceObjectExists[(existingObject.Name, existingObject.AddressId)] = true;
            }

            return cache;
        }
    }
}
