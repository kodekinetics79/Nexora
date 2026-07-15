using ERP_RFQ_Automation.DTOs.BusinessUnit;
using ERP_RFQ_Automation.DTOs.TeamDTOs;
using ERP_RFQ_Automation.DTOs.UserDTO;
using ERP_RFQ_Automation.DTOs.UserGroup;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class UserController : ControllerBase
    {
        private readonly IUserRepository _repository;
        private readonly IWebHostEnvironment _environment;
        private static readonly int[] AllowedPageSizes = { 5, 10, 25, 50 };

        public UserController(IUserRepository repository, IWebHostEnvironment environment)
        {
            _repository = repository;
            _environment = environment;
        }

        // GET: api/User?pageNumber=1&pageSize=10&id=1&userName=john&email=john@example.com&roleId=1&region=US&isActive=true&businessUnitId=1
        [HttpGet]
        public async Task<ActionResult<DTOs.UserDTO.PaginatedResponseDTO<UserResponseDTO>>> GetAll(
            [FromQuery] long? businessUnitId = null,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] long? id = null,
            [FromQuery] string? userName = null,
            [FromQuery] string? email = null,
            [FromQuery] long? roleId = null,
            [FromQuery] string? region = null,
            [FromQuery] bool? isActive = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = businessUnitId ?? claimBUId;

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                if (pageNumber < 1)
                    return BadRequest("Page number must be greater than or equal to 1.");
                
                // Relaxed validation: Allow any page size up to 1000
                if (pageSize < 1 || pageSize > 1000)
                    return BadRequest("Page size must be between 1 and 1000.");

                var (users, totalCount) = await _repository.GetAllAsync(pageNumber, pageSize, id, userName, email, roleId, region, isActive, targetBUId);

                var response = new DTOs.UserDTO.PaginatedResponseDTO<UserResponseDTO>
                {
                    Items = users,
                    TotalCount = totalCount,
                    PageNumber = pageNumber,
                    PageSize = pageSize
                };

                return Ok(response);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving data: {ex.Message}");
            }
        }

        // GET: api/User/5
        [HttpGet("{id}")]
        public async Task<ActionResult<UserResponseDTO>> GetById(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = businessUnitId ?? claimBUId;

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var user = await _repository.GetByIdAsync(id, targetBUId);
                return Ok(MapToResponse(user));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving data: {ex.Message}");
            }
        }

        // POST: api/User
        [HttpPost]
        public async Task<ActionResult<UserResponseDTO>> Create([FromForm] UserCreateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                string? imagePath = null;
                if (request.ImageFile != null)
                {
                    var uploadsFolder = Path.Combine(_environment.WebRootPath, "UserImages");
                    if (!Directory.Exists(uploadsFolder))
                        Directory.CreateDirectory(uploadsFolder);

                    var ext = Path.GetExtension(request.ImageFile.FileName);
                    var uniqueFileName = $"{Guid.NewGuid()}{ext}";
                    var filePath = Path.Combine(uploadsFolder, uniqueFileName);
                    using (var fileStream = new FileStream(filePath, FileMode.Create))
                    {
                        await request.ImageFile.CopyToAsync(fileStream);
                    }
                    imagePath = $"/UserImages/{uniqueFileName}";
                }

                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (request.Buid <= 0) request.Buid = claimBUId;

                if (request.Buid <= 0) return BadRequest("Business Unit ID is required.");

                var user = new User
                {
                    FirstName = request.FirstName,
                    MiddleName = request.MiddleName,
                    LastName = request.LastName,
                    Email = request.Email,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
                    ImageUrl = imagePath ?? request.ImageUrl ?? string.Empty,
                    RoleId = request.RoleId,
                    TeamId = request.TeamId,
                    Timezone = request.Timezone,
                    Region = request.Region,
                    ManagerId = request.ManagerId,
                    Buid = request.Buid,
                    UserGroupId = request.UserGroupId,
                    IsActive = request.IsActive ?? true,
                    CreatedBy = User.Identity?.Name ?? request.CreatedBy ?? "System",
                    CreatedOn = DateTime.UtcNow
                };

                await _repository.AddAsync(user);

                var response = MapToResponse(user);
                return CreatedAtAction(nameof(GetById), new { id = user.Id, businessUnitId = user.Buid }, response);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error creating data: {ex.Message}");
            }
        }

        // PUT: api/User/5
        [HttpPut("{id}")]
        public async Task<ActionResult> Update(long id, [FromForm] UserUpdateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (request.Buid <= 0) request.Buid = claimBUId;

                if (request.Buid <= 0) return BadRequest("Business Unit ID is required.");

                var existing = await _repository.GetByIdAsync(id, request.Buid);

                string? imagePath = existing.ImageUrl;
                if (request.ImageFile != null)
                {
                    var uploadsFolder = Path.Combine(_environment.WebRootPath, "UserImages");
                    if (!Directory.Exists(uploadsFolder))
                        Directory.CreateDirectory(uploadsFolder);

                    var ext = Path.GetExtension(request.ImageFile.FileName);
                    var uniqueFileName = $"{Guid.NewGuid()}{ext}";
                    var filePath = Path.Combine(uploadsFolder, uniqueFileName);
                    using (var fileStream = new FileStream(filePath, FileMode.Create))
                    {
                        await request.ImageFile.CopyToAsync(fileStream);
                    }
                    imagePath = $"/UserImages/{uniqueFileName}";
                }

                existing.FirstName = request.FirstName;
                existing.MiddleName = request.MiddleName;
                existing.LastName = request.LastName;
                existing.Email = request.Email;
                existing.ImageUrl = imagePath ?? request.ImageUrl;
                
                // Update Foreign Keys
                existing.RoleId = request.RoleId;
                existing.TeamId = request.TeamId;
                existing.UserGroupId = request.UserGroupId;
                existing.ManagerId = request.ManagerId;
                existing.Buid = request.Buid;
                existing.Timezone = request.Timezone;
                existing.Region = request.Region;
                existing.IsActive = request.IsActive ?? true;

                // Nullify navigation properties to ensure FK changes are respected during Update
                existing.Role = null;
                existing.Team = null;
                existing.UserGroup = null;
                existing.Manager = null;
                existing.Bu = null;

                existing.ModifiedBy = User.Identity?.Name ?? request.ModifiedBy ?? "System";
                existing.ModifiedOn = DateTime.UtcNow;

                await _repository.UpdateAsync(existing);

                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error updating data: {ex.Message}");
            }
        }

        // POST: api/User/ChangePassword/5
        [HttpPost("{id}/ChangePassword")]
        public async Task<ActionResult> ChangePassword(long id, [FromBody] ChangePasswordRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                await _repository.ChangePasswordAsync(id, request.NewPassword);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error changing password: {ex.Message}");
            }
        }

        // DELETE: api/User/5
        [HttpDelete("{id}")]
        public async Task<ActionResult> Delete(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = businessUnitId ?? claimBUId;

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                await _repository.DeleteAsync(id, targetBUId);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error deleting data: {ex.Message}");
            }
        }

        // GET: api/User/Roles
        [HttpGet("Roles")]
        public async Task<ActionResult<IEnumerable<RoleResponseDTO>>> GetRoles()
        {
            try
            {
                var roles = await _repository.GetRolesAsync();
                return Ok(roles);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving roles: {ex.Message}");
            }
        }

        // GET: api/User/Teams
        [HttpGet("Teams")]
        public async Task<ActionResult<IEnumerable<TeamResponseDTO>>> GetTeams([FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = businessUnitId ?? claimBUId;

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var teams = await _repository.GetTeamsAsync(targetBUId);
                return Ok(teams);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving teams: {ex.Message}");
            }
        }

        // GET: api/User/BusinessUnits
        [HttpGet("BusinessUnits")]
        public async Task<ActionResult<IEnumerable<BusinessUnitResponseDTO>>> GetBusinessUnits()
        {
            try
            {
                var businessUnits = await _repository.GetBusinessUnitsAsync();
                return Ok(businessUnits);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving business units: {ex.Message}");
            }
        }

    
        // GET: api/User/UserGroups
        [HttpGet("UserGroups")]
        public async Task<ActionResult<IEnumerable<UserGroupResponseDTO>>> GetUserGroups([FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = businessUnitId ?? claimBUId;

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var userGroups = await _repository.GetUserGroupsAsync(targetBUId);
                return Ok(userGroups);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving user groups: {ex.Message}");
            }
        }

        private UserResponseDTO MapToResponse(User user)
        {
            return new UserResponseDTO
            {
                Id = user.Id,
                FirstName = user.FirstName,
                MiddleName = user.MiddleName,
                LastName = user.LastName,
                Email = user.Email,
                ImageUrl = user.ImageUrl,
                RoleId = user.RoleId,
                TeamId = user.TeamId,
                Timezone = user.Timezone,
                LastLogin = user.LastLogin,
                Region = user.Region,
                ManagerId = user.ManagerId,
                Buid = user.Buid,
                UserGroupId = user.UserGroupId,
                IsActive = user.IsActive,
                CreatedBy = user.CreatedBy,
                CreatedOn = user.CreatedOn,
                ModifiedBy = user.ModifiedBy,
                ModifiedOn = user.ModifiedOn,
                RoleName = user.Role != null ? user.Role.SetupValue : null,
                TeamName = user.Team != null ? user.Team.TeamName : null,
                BusinessUnitName = user.Bu != null ? user.Bu.BusinessUnitName : null,
                UserGroupName = user.UserGroup != null ? user.UserGroup.UserGroupsName : null
            };
        }
    }
}