using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.AuthDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Platform.Entitlements;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace ERP_RFQ_Automation.Repositories
{
    public class AuthRepository : IAuthRepository
    {
        private readonly ErpRfqAutomationContext _context;
        private readonly IConfiguration _configuration;
        private readonly ILogger<AuthRepository> _logger;
        private readonly ITenantAccessService? _tenantAccess;
        private readonly IRoleGate? _roleGate;

        public AuthRepository(
            ErpRfqAutomationContext context,
            IConfiguration configuration,
            ILogger<AuthRepository> logger,
            ITenantAccessService? tenantAccess = null,
            IRoleGate? roleGate = null)
        {
            _context = context;
            _configuration = configuration;
            _logger = logger;
            _tenantAccess = tenantAccess;
            // Fall back to a self-constructed gate rather than leaving this null. A null
            // gate made ResolveRoleAuthorityAsync return (false, false) for EVERY user,
            // so a caller that omitted the optional argument silently reported the tenant's
            // super admin as an ordinary user — the login response then hid the admin
            // surfaces and the UI rendered as "unwired". That degradation was invisible:
            // no exception, no log, just an authority answer of "no" for everyone.
            _roleGate = roleGate ?? new RoleGate(context, new MemoryCache(new MemoryCacheOptions()));
        }

        public async Task<LoginResponseDTO> LoginAsync(LoginRequestDTO request)
        {
            _logger.LogInformation("Attempting login for email: {Email}", request.Email);

            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            {
                _logger.LogWarning("Login attempt with missing credentials.");
                throw new UnauthorizedAccessException("Email and password are required.");
            }

            // The tenant is derived from the email: fetch every user with this
            // email (normally exactly one; the same email may exist in multiple
            // business units in the future).
            var usersForEmail = await _context.Users
                .Include(u => u.Role)
                .Include(u => u.Bu) // Include BusinessUnit for BusinessUnitName
                .AsNoTracking()
                .Where(u => u.Email.ToLower() == request.Email.ToLower())
                .ToListAsync();

            User user;

            if (request.BusinessUnitId.HasValue)
            {
                // Backward-compatible path (also used when the client retries
                // after a business-unit selection prompt): behave exactly as the
                // original email+password+businessUnitId login did.
                var candidate = usersForEmail.FirstOrDefault(u => u.Buid == request.BusinessUnitId.Value)
                                ?? usersForEmail.FirstOrDefault();

                if (candidate == null)
                {
                    _logger.LogWarning("User not found for email: {Email}", request.Email);
                    throw new UnauthorizedAccessException("Invalid email or password.");
                }

                if (!BCrypt.Net.BCrypt.Verify(request.Password, candidate.PasswordHash))
                {
                    _logger.LogWarning("Password verification failed for email: {Email}", request.Email);
                    throw new UnauthorizedAccessException("Invalid email or password.");
                }

                if (candidate.Buid != request.BusinessUnitId)
                {
                    _logger.LogWarning("Business Unit mismatch for email: {Email}. Expected: {Expected}, Received: {Received}",
                        request.Email, candidate.Buid, request.BusinessUnitId);
                    throw new UnauthorizedAccessException("Invalid Business Unit selected for this user.");
                }

                user = candidate;
            }
            else
            {
                // Email-only login: verify the password against the active
                // account(s) for this email. All failures (unknown email, wrong
                // password) surface the same message so nothing is leaked about
                // which part failed or which business units exist.
                var verifiedUsers = usersForEmail
                    .Where(u => u.IsActive == true)
                    .Where(u => BCrypt.Net.BCrypt.Verify(request.Password, u.PasswordHash))
                    .ToList();

                if (verifiedUsers.Count == 0)
                {
                    // Preserve the distinct "inactive" message when the
                    // credentials are actually correct for an inactive account.
                    var inactiveMatch = usersForEmail
                        .Where(u => u.IsActive != true)
                        .Any(u => BCrypt.Net.BCrypt.Verify(request.Password, u.PasswordHash));

                    if (inactiveMatch)
                    {
                        _logger.LogWarning("Inactive account login attempt for email: {Email}", request.Email);
                        throw new UnauthorizedAccessException("User account is not active.");
                    }

                    _logger.LogWarning("Invalid credentials for email: {Email}", request.Email);
                    throw new UnauthorizedAccessException("Invalid email or password.");
                }

                if (verifiedUsers.Count > 1)
                {
                    // Same email + password valid in multiple business units:
                    // ask the client to pick one (no token issued).
                    _logger.LogInformation("Email {Email} matches multiple business units; requesting selection.", request.Email);

                    return new LoginResponseDTO
                    {
                        RequiresBusinessUnitSelection = true,
                        BusinessUnits = verifiedUsers
                            .Where(u => u.Buid.HasValue)
                            .Select(u => new LoginBusinessUnitOptionDTO
                            {
                                Id = u.Buid!.Value,
                                Name = u.Bu?.BusinessUnitName ?? $"Business Unit {u.Buid.Value}"
                            })
                            .ToList()
                    };
                }

                user = verifiedUsers[0];
            }

            if (!user.IsActive == true)
            {
                _logger.LogWarning("Inactive account login attempt for email: {Email}", request.Email);
                throw new UnauthorizedAccessException("User account is not active.");
            }

            // Tenant-status enforcement (P0): a PastDue/Suspended/Archived platform Tenant must
            // never receive a token, even with valid credentials. Runs AFTER password
            // verification so nothing about tenant existence leaks to guessers, and admits
            // legacy business units without a platform Tenant row (resolved, no tenant).
            //
            // Sec-D1: an UNRESOLVABLE platform plane no longer mints a token either. A token
            // issued here outlives the outage that let it out — it stays valid for its whole
            // lifetime and the status guard will not re-check it until it expires — so this is
            // the one gate where failing open leaves a durable hole rather than a momentary one.
            if (_tenantAccess is not null && user.Buid is { } tenantBusinessUnitId)
            {
                var access = await _tenantAccess.GetAccessAsync(tenantBusinessUnitId);
                if (access.IsUnresolvable)
                {
                    _logger.LogError(
                        "Login refused for email {Email}: the platform plane could not be read for "
                        + "business unit {BusinessUnitId}, so no token was issued.",
                        request.Email, tenantBusinessUnitId);
                    throw new TenantAccessUnresolvableException(tenantBusinessUnitId, access.UnresolvedReason);
                }

                if (access.IsAccessDenied)
                {
                    _logger.LogWarning(
                        "Login blocked for email {Email}: tenant for business unit {BusinessUnitId} is {Status}.",
                        request.Email, tenantBusinessUnitId, access.Status);
                    throw new TenantAccessDeniedException(tenantBusinessUnitId, access.Status);
                }
            }

            _logger.LogInformation("Login successful for email: {Email}", request.Email);

            await RecordLastLoginAsync(user);

            var userName = user.FirstName +
                           (string.IsNullOrEmpty(user.MiddleName) ? "" : " " + user.MiddleName) +
                           (string.IsNullOrEmpty(user.LastName) ? "" : " " + user.LastName);

            var token = GenerateJwtToken(user, userName);

            // RC-8: this used to answer "is this a super admin?" from its OWN copy of the
            // name-matching rule, applied to a plain FK Include with no business-unit, SetupType or
            // IsActive predicate — while RoleGate.ResolveRoleAsync requires all three. When the two
            // disagreed the UI rendered the whole product and every API call 403'd. There is now
            // exactly ONE authority for that question, and this is a call into it.
            var (isSuperAdmin, isManager) = await ResolveRoleAuthorityAsync(user);

            return new LoginResponseDTO
            {
                Id = user.Id,
                Email = user.Email,
                UserName = userName,
                RoleId = user.RoleId,
                RoleName = user.Role?.SetupValue ?? "No Role Assigned",
                IsSuperAdmin = isSuperAdmin,
                IsManager = isManager,
                BusinessUnitId = user.Buid,
                BusinessUnitName = user.Bu?.BusinessUnitName,
                Token = token
            };
        }

        private async Task<(bool IsSuperAdmin, bool IsManager)> ResolveRoleAuthorityAsync(User user)
        {
            if (_roleGate is null || user.RoleId is not { } roleId || user.Buid is not { } businessUnitId)
                return (false, false);

            return (
                await _roleGate.IsSuperAdminAsync(roleId, businessUnitId),
                await _roleGate.IsManagerOrAdminAsync(roleId, businessUnitId));
        }

        /// <summary>
        /// Best-effort <c>Users.LastLogin</c> stamp, mirroring
        /// <c>Platform/Auth/PlatformAuthService.cs</c>. The column existed and was surfaced in the
        /// users grid but was never written, so "last active" read blank for every account — and
        /// that is the column an access review needs to spot dormant privileged users. A failure
        /// here must never block an otherwise valid login.
        /// </summary>
        private async Task RecordLastLoginAsync(User user)
        {
            try
            {
                var tracked = new User { Id = user.Id };
                _context.Users.Attach(tracked);
                tracked.LastLogin = DateTime.UtcNow;
                _context.Entry(tracked).Property(u => u.LastLogin).IsModified = true;
                await _context.SaveChangesAsync();
                _context.Entry(tracked).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not update LastLogin for user {UserId}.", user.Id);
            }
        }

        private string GenerateJwtToken(User user, string userName)
        {
            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                // RC-7: without a name claim — and the matching NameClaimType in Program.cs —
                // User.Identity.Name was ALWAYS null, which is exactly why every "CreatedBy"
                // expression in the IAM controllers fell through to the client-supplied value.
                new Claim(JwtRegisteredClaimNames.Name, string.IsNullOrWhiteSpace(userName) ? user.Email : userName),
                new Claim("roleId", user.RoleId?.ToString() ?? "none"),
                new Claim("businessUnitId", user.Buid?.ToString() ?? "none"), // Include BusinessUnitId in token
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _configuration["Jwt:Issuer"],
                audience: _configuration["Jwt:Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(double.Parse(_configuration["Jwt:ExpiryMinutes"])),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
