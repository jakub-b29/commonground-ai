using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;

namespace CommonGround.Application.Interfaces;

public interface IMembershipService
{
    Task<FirmMembership> RequestJoinByFirmCodeAsync(
        string firmCode,
        Guid userId,
        WorkspaceSecurityRole requestedRole,
        Guid? communityId,
        string? unitNumber,
        string? note,
        CancellationToken ct = default);

    Task<FirmMembership> ApproveMembershipAsync(
        Guid actorMembershipId,
        Guid membershipId,
        WorkspaceSecurityRole role,
        WorkspacePermissionFlags permissionFlags,
        IReadOnlyList<Guid> propertyIds,
        Guid? unitId,
        CancellationToken ct = default);

    Task<FirmMembership> RevokeMembershipAsync(
        Guid actorMembershipId,
        Guid membershipId,
        CancellationToken ct = default);

    Task<IReadOnlyList<FirmMembership>> GetPendingApprovalsAsync(
        Guid firmId,
        CancellationToken ct = default);

    Task<IReadOnlyList<FirmMembership>> GetPendingApprovalsAsync(
        Guid firmId,
        Guid actorMembershipId,
        CancellationToken ct = default);
}
