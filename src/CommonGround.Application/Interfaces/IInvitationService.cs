using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;

namespace CommonGround.Application.Interfaces;

public sealed record InvitationCreationResult(
    Invitation Invitation,
    string PlainToken);

public sealed record InvitationPreview(
    Guid InvitationId,
    string FirmName,
    string FirmGroupCode,
    string MaskedRecipientEmail,
    string TargetRole,
    string TargetPropertyIds,
    DateTime ExpiresAtUtc,
    bool IsExpired,
    string Status);

public interface IInvitationService
{
    Task<InvitationCreationResult> CreateInvitationAsync(
        Guid firmId,
        Guid actorMembershipId,
        string recipientEmail,
        string recipientName,
        WorkspaceSecurityRole targetRole,
        WorkspacePermissionFlags grantedPermissions,
        bool requiresApproval,
        IReadOnlyList<Guid> propertyIds,
        Guid? targetUnitId,
        DateTime? expiresAtUtc,
        CancellationToken ct = default);

    Task<InvitationPreview?> GetInvitationPreviewAsync(
        string tokenHash,
        CancellationToken ct = default);

    Task<FirmMembership> AcceptInvitationAsync(
        string plainToken,
        Guid userId,
        CancellationToken ct = default);

    Task<Invitation> RevokeInvitationAsync(
        Guid invitationId,
        Guid actorMembershipId,
        CancellationToken ct = default);
}
