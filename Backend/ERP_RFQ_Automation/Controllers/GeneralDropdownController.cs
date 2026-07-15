using ERP_RFQ_Automation.DTOs.GeneralDropdown;
using ERP_RFQ_Automation.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class GeneralDropdownController : ControllerBase
    {
        private readonly IGeneralDropdownRepository _repository;
        public GeneralDropdownController(IGeneralDropdownRepository repository)
        {
            _repository = repository;
        }

  
        [HttpGet("countries")]
        public async Task<ActionResult<IEnumerable<GeneralDropdownDto>>> GetCountries(
            [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var countries = await _repository.GetCountriesAsync(targetBUId);
                return Ok(countries);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }


        [HttpGet("states")]
        public async Task<ActionResult<IEnumerable<GeneralDropdownDto>>> GetStates(
            [FromQuery] long? businessUnitId = null, [FromQuery] int? countryId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var states = await _repository.GetStatesAsync(targetBUId, countryId);
                return Ok(states);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }


        [HttpGet("cities")]
        public async Task<ActionResult<IEnumerable<GeneralDropdownDto>>> GetCities(
            [FromQuery] long? businessUnitId = null, [FromQuery] int? stateId = null, [FromQuery] int? countryId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var cities = await _repository.GetCitiesAsync(targetBUId, stateId, countryId);
                return Ok(cities);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }


        [HttpGet("categories")]
        public async Task<ActionResult<IEnumerable<GeneralDropdownDto>>> GetCategories(
            [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var categories = await _repository.GetCategoriesAsync(targetBUId);
                return Ok(categories);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }


        [HttpGet("warehouses")]
        public async Task<ActionResult<IEnumerable<GeneralDropdownDto>>> GetWarehouses(
            [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var warehouses = await _repository.GetWarehousesAsync(targetBUId);
                return Ok(warehouses);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }


        [HttpGet("suppliers")]
        public async Task<ActionResult<IEnumerable<GeneralDropdownDto>>> GetSuppliers(
            [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var suppliers = await _repository.GetSuppliersAsync(targetBUId);
                return Ok(suppliers);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }


        [HttpGet("statuses")]
        public async Task<ActionResult<IEnumerable<GeneralDropdownDto>>> GetStatuses(
            [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var statuses = await _repository.GetStatusesAsync(targetBUId);
                return Ok(statuses);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }

 
        
    }
}