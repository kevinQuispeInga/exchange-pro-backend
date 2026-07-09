using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UESAN.ExchangePro.CORE.Core.DTOs;
using UESAN.ExchangePro.CORE.Core.Entities;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class DatosPagoController : ControllerBase
{
    private readonly IDatosPagoRepository _repo;
    public DatosPagoController(IDatosPagoRepository repo) => _repo = repo;

    [HttpGet]
    public async Task<IActionResult> ObtenerDatosPagoUsuarioAutenticado()
    {
        var idUsuarioClaim = User.FindFirst("IdUsuario")?.Value;
        if (string.IsNullOrEmpty(idUsuarioClaim))
            return Unauthorized("Usuario no identificado.");

        long idUsuario = long.Parse(idUsuarioClaim);
        var datos = await _repo.GetByUsuario(idUsuario);
        var primerDato = datos.FirstOrDefault();

        if (primerDato == null)
        {
            return Ok(new DatosPagoResponseDTO()); // Retornar objeto vacio si no hay registros
        }

        var result = new DatosPagoResponseDTO
        {
            IdDatoPago = primerDato.IdDatoPago,
            Yape = primerDato.Yape,
            Plin = primerDato.Plin,
            IdBanco = primerDato.IdBanco,
            NumeroCuenta = primerDato.NumeroCuenta,
            Cci = primerDato.Cci,
            BancoNombre = primerDato.IdBancoNavigation?.Nombre
        };
        return Ok(result);
    }

    [HttpGet("{idUsuario}")]
    public async Task<IActionResult> ObtenerDatosPago(long idUsuario)
    {
        var datos = await _repo.GetByUsuario(idUsuario);
        var result = datos.Select(d => new DatosPagoResponseDTO
        {
            IdDatoPago = d.IdDatoPago,
            Yape = d.Yape,
            Plin = d.Plin,
            IdBanco = d.IdBanco,
            NumeroCuenta = d.NumeroCuenta,
            Cci = d.Cci,
            BancoNombre = d.IdBancoNavigation?.Nombre
        });
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> AgregarDatoPago([FromBody] CrearDatosPagoDTO dto)
    {
        var idUsuarioClaim = User.FindFirst("IdUsuario")?.Value;
        if (string.IsNullOrEmpty(idUsuarioClaim))
            return Unauthorized("Usuario no identificado.");

        long idUsuario = long.Parse(idUsuarioClaim);

        var existentes = await _repo.GetByUsuario(idUsuario);
        var existente = existentes.FirstOrDefault();

        if (existente != null)
        {
            existente.Yape = dto.Yape;
            existente.Plin = dto.Plin;
            existente.IdBanco = dto.IdBanco;
            existente.NumeroCuenta = dto.NumeroCuenta;
            existente.Cci = dto.Cci;

            bool exito = await _repo.Update(existente);
            if (exito)
                return Ok(new { mensaje = "Método de pago actualizado correctamente" });
            return BadRequest("No se pudo actualizar el método de pago");
        }
        else
        {
            var datos = new DatosPagoUsuario
            {
                IdUsuario = idUsuario,
                Yape = dto.Yape,
                Plin = dto.Plin,
                IdBanco = dto.IdBanco,
                NumeroCuenta = dto.NumeroCuenta,
                Cci = dto.Cci
            };

            bool exito = await _repo.Insert(datos);
            if (exito)
                return Ok(new { mensaje = "Método de pago agregado correctamente" });
            return BadRequest("No se pudo guardar el método de pago");
        }
    }
}