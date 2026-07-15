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

            var user = await _context.Users
                .Include(u => u.Role)
                .Include(u => u.Bu) // Include BusinessUnit for BusinessUnitName
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Email.ToLower() == request.Email.ToLower());

            if (user == null)
            {
                _logger.LogWarning("User not found for email: {Email}", request.Email);
                throw new UnauthorizedAccessException("Invalid email or password.");
            }

            bool isPasswordValid = BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash);

            if (!isPasswordValid)
            {
                _logger.LogWarning("Password verification failed for email: {Email}", request.Email);
                throw new UnauthorizedAccessException("Invalid email or password.");
            }

            if (user.Buid != request.BusinessUnitId)
            {
                _logger.LogWarning("Business Unit mismatch for email: {Email}. Expected: {Expected}, Received: {Received}", 
                    request.Email, user.Buid, request.BusinessUnitId);
                throw new UnauthorizedAccessException("Invalid Business Unit selected for this user.");
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