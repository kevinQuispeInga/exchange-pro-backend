using Microsoft.AspNetCore.Mvc;
using UESAN.ExchangePro.CORE.Core.DTOs;
using UESAN.ExchangePro.CORE.Core.Interfaces;
using System;

namespace UESAN.ExchangePro.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;

        public AuthController(IAuthService authService)
        {
            _authService = authService;
        }

        [HttpPost("registrar")]
        public async Task<IActionResult> Registrar([FromBody] RegistroDTO registroDTO)
        {
            try
            {
                bool resultado = await _authService.Registrar(registroDTO);
                if (resultado)
                    return Ok(new { mensaje = "Usuario registrado y Wallet creada con éxito." });

                return BadRequest(new { error = "No se pudo registrar el usuario." });
            }
            catch (Exception ex)
            {
                var mensajeAmigable = ObtenerMensajeErrorAmigable(ex, registroDTO);
                return BadRequest(new { error = mensajeAmigable });
            }
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDTO loginDTO)
        {
            try
            {
                string token = await _authService.Login(loginDTO);
                return Ok(new { token = token });
            }
            catch (Exception ex)
            {
                return Unauthorized(new { error = ex.Message });
            }
        }

        [HttpPost("solicitar-reset")]
        public async Task<IActionResult> SolicitarReset([FromBody] SolicitarResetDTO dto)
        {
            try
            {
                await _authService.SolicitarReset(dto);
                return Ok(new { mensaje = "Si el correo está registrado, recibirás un enlace de recuperación." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("restablecer-password")]
        public async Task<IActionResult> RestablecerPassword([FromBody] RestablecerPasswordDTO dto)
        {
            try
            {
                bool exito = await _authService.RestablecerPassword(dto);
                if (exito)
                    return Ok(new { mensaje = "Contraseña restablecida correctamente." });
                return BadRequest(new { error = "No se pudo restablecer la contraseña." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        private string ObtenerMensajeErrorAmigable(Exception ex, RegistroDTO dto)
        {
            var innerMsg = ex.InnerException?.Message ?? "";
            
            // Si es un error de llave única de SQL Server
            if (innerMsg.Contains("Violation of UNIQUE KEY constraint") || innerMsg.Contains("duplicate key"))
            {
                int startIndex = innerMsg.IndexOf("duplicate key value is (");
                if (startIndex != -1)
                {
                    startIndex += "duplicate key value is (".Length;
                    int endIndex = innerMsg.IndexOf(")", startIndex);
                    if (endIndex != -1)
                    {
                        var valDuplicado = innerMsg.Substring(startIndex, endIndex - startIndex).Trim();
                        
                        if (valDuplicado.Equals(dto.DocumentoIdentidad, StringComparison.OrdinalIgnoreCase))
                            return $"El documento de identidad '{dto.DocumentoIdentidad}' ya se encuentra registrado.";
                        if (valDuplicado.Equals(dto.Correo, StringComparison.OrdinalIgnoreCase))
                            return $"El correo electrónico '{dto.Correo}' ya se encuentra registrado.";
                        if (valDuplicado.Equals(dto.Telefono, StringComparison.OrdinalIgnoreCase))
                            return $"El número de teléfono '{dto.Telefono}' ya se encuentra registrado.";
                    }
                }
                
                // Fallback en caso de que no coincida exactamente con los campos
                return "Ya existe un usuario registrado con algunos de estos datos (Correo, Teléfono o Documento de Identidad).";
            }
            
            return ex.Message;
        }
    }
}