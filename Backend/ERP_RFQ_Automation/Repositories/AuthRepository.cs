using ERP_RFQ_Automation.DTOs.AuthDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
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

        public AuthRepository(ErpRfqAutomationContext context, IConfiguration configuration, ILogger<AuthRepository> logger)
        {
            _context = context;
            _configuration = configuration;
            _logger = logger;
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

            _logger.LogInformation("Login successful for email: {Email}", request.Email);

            var token = GenerateJwtToken(user);

            return new LoginResponseDTO
            {
                Id = user.Id,
                Email = user.Email,
                UserName = user.FirstName +
                           (string.IsNullOrEmpty(user.MiddleName) ? "" : " " + user.MiddleName) +
                           (string.IsNullOrEmpty(user.LastName) ? "" : " " + user.LastName),
                RoleId = user.RoleId,
                RoleName = user.Role?.SetupValue ?? "No Role Assigned",
                BusinessUnitId = user.Buid,
                BusinessUnitName = user.Bu?.BusinessUnitName,
                Token = token
            };
        }

        private string GenerateJwtToken(User user)
        {
            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
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