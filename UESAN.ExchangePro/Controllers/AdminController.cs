using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using UESAN.ExchangePro.CORE.Core.Interfaces;

namespace UESAN.ExchangePro.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class AdminController : ControllerBase
    {
        private readonly IAdminService _adminService;

        public AdminController(IAdminService adminService)
        {
            _adminService = adminService;
        }

        [HttpGet("estadisticas")]
        public async Task<IActionResult> GetEstadisticas()
        {
            try
            {
                var rolClaim = User.FindFirst("Rol")?.Value;
                if (rolClaim != "2")
                    return Forbid();

                var stats = await _adminService.GetEstadisticas();
                return Ok(stats);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpGet("historial")]
        public async Task<IActionResult> GetHistorial([FromQuery] string metrica, [FromQuery] int? anio, [FromQuery] int? mes, [FromQuery] int? semana)
        {
            try
            {
                var rolClaim = User.FindFirst("Rol")?.Value;
                if (rolClaim != "2")
                    return Forbid();

                if (string.IsNullOrEmpty(metrica))
                    return BadRequest(new { error = "metrica es obligatoria" });

                var data = await _adminService.GetHistorial(metrica, anio, mes, semana);
                return Ok(data);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("chatbot")]
        [AllowAnonymous]
        public async Task<IActionResult> Chatbot([FromBody] ChatbotRequestDTO request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.Mensaje))
                    return BadRequest(new { error = "El mensaje no puede estar vacío" });

                var response = await _adminService.ChatbotResponder(request.Mensaje);
                return Ok(response);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }
    }

    public class ChatbotRequestDTO
    {
        public string Mensaje { get; set; } = null!;
    }
}
