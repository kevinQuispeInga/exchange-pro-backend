using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Linq;
using System.Threading.Tasks;
using UESAN.ExchangePro.CORE.Core.DTOs;
using UESAN.ExchangePro.CORE.Core.Entities;
using UESAN.ExchangePro.CORE.Core.Interfaces;

namespace UESAN.ExchangePro.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class OfertaController : ControllerBase
    {
        private readonly IOfertaRepository _repo;
        private readonly IWalletRepository _walletRepo; // Agregamos el repositorio de Wallets

        public OfertaController(IOfertaRepository repo, IWalletRepository walletRepo)
        {
            _repo = repo;
            _walletRepo = walletRepo;
        }

        // POST: api/Oferta
        [HttpPost]
        [Authorize] // Requiere token para publicar
        public async Task<IActionResult> PublicarOferta([FromBody] PublicarOfertaDTO dto)
        {
            // 1. Extraer ID del usuario desde el Token JWT
            var idUsuarioClaim = User.FindFirst("IdUsuario")?.Value;
            if (string.IsNullOrEmpty(idUsuarioClaim))
                return Unauthorized("Token inválido o usuario no identificado.");

            long idUsuario = long.Parse(idUsuarioClaim);

            // 2. LÓGICA DE ESCROW: Validar y retener los fondos
            var wallet = await _walletRepo.GetByUsuarioId(idUsuario);
            if (wallet == null)
                return BadRequest("No tienes una billetera activa.");

            // Determinamos qué moneda revisar según el tipo de operación
            //   VENTA: el usuario vende MonedaEntrega → debe tener saldo en MonedaEntrega
            //   COMPRA: el usuario compra MonedaEntrega y paga con MonedaRecibe → debe tener saldo en MonedaRecibe
            int monedaARevisar = dto.TipoOperacion == "COMPRA" ? dto.MonedaRecibe : dto.MonedaEntrega;
            decimal montoRequerido = dto.TipoOperacion == "COMPRA"
                ? dto.MontoOfertado * dto.TasaCambio
                : dto.MontoOfertado;

            var saldoMoneda = wallet.WalletSaldos.FirstOrDefault(s => s.IdMoneda == monedaARevisar);

            // Validamos que tenga fondos suficientes
            if (saldoMoneda == null || saldoMoneda.SaldoDisponible < montoRequerido)
            {
                return BadRequest("FONDOS INSUFICIENTES: Necesitas recargar tu billetera antes de publicar esta oferta.");
            }

            // Aplicamos la retención restando del disponible y sumando al retenido
            saldoMoneda.SaldoDisponible -= montoRequerido;
            saldoMoneda.SaldoRetenido = (saldoMoneda.SaldoRetenido ?? 0) + montoRequerido;

            // Guardamos los cambios en la billetera
            bool walletActualizada = await _walletRepo.Update(wallet);
            if (!walletActualizada)
                return StatusCode(500, "Error interno al intentar retener los fondos.");

            // 3. Crear el objeto Oferta
            var oferta = new Ofertas
            {
                IdUsuario = idUsuario,
                MonedaEntrega = dto.MonedaEntrega,
                MonedaRecibe = dto.MonedaRecibe,
                MontoOfertado = dto.MontoOfertado,
                MontoMinimo = dto.MontoMinimo,
                TasaCambio = dto.TasaCambio,
                TipoOperacion = dto.TipoOperacion,
                FechaPublicacion = DateTime.UtcNow,
                Estado = "ACTIVA"
            };

            // 4. Guardar la Oferta en la base de datos
            bool resultado = await _repo.Insert(oferta);

            if (resultado)
                return Ok(new { mensaje = "Oferta publicada exitosamente. Tus fondos han sido retenidos por seguridad." });

            // 5. ROLLBACK MANUAL: Si la oferta falló en guardarse, devolvemos el dinero al saldo disponible
            saldoMoneda.SaldoDisponible += montoRequerido;
            saldoMoneda.SaldoRetenido -= montoRequerido;
            await _walletRepo.Update(wallet);

            return BadRequest("Error al intentar publicar la oferta. Tus fondos han sido devueltos a tu saldo disponible.");
        }

        // GET: api/Oferta
        [HttpGet]
        public async Task<IActionResult> ListarOfertas()
        {
            var ofertas = await _repo.GetAllActivas();

            // Mapeo a DTO para evitar error 500 (referencias circulares) y ocultar datos sensibles
            var listaDTO = ofertas.Select(o => new OfertaResponseDTO
            {
                IdOferta = o.IdOferta,
                IdUsuario = o.IdUsuario,
                NombreUsuario = o.IdUsuarioNavigation?.NombreCompleto ?? "Desconocido",
                MontoOfertado = o.MontoOfertado,
                MontoMinimo = o.MontoMinimo,
                TasaCambio = o.TasaCambio,
                TipoOperacion = o.TipoOperacion,
                Estado = o.Estado,
                MonedaEntrega = o.MonedaEntrega,
                MonedaRecibe = o.MonedaRecibe,
                MonedaEntregaCode = o.MonedaEntregaNavigation?.Codigo,
                MonedaRecibeCode = o.MonedaRecibeNavigation?.Codigo,
                FechaPublicacion = o.FechaPublicacion
            });

            return Ok(listaDTO);
        }

        // GET: api/Oferta/mis-ofertas
        [HttpGet("mis-ofertas")]
        [Authorize]
        public async Task<IActionResult> GetMisOfertas()
        {
            var idUsuarioClaim = User.FindFirst("IdUsuario")?.Value;
            if (string.IsNullOrEmpty(idUsuarioClaim))
                return Unauthorized();

            long idUsuario = long.Parse(idUsuarioClaim);
            var ofertas = await _repo.GetByUsuario(idUsuario);

            var listaDTO = ofertas.Select(o => new OfertaResponseDTO
            {
                IdOferta = o.IdOferta,
                IdUsuario = o.IdUsuario,
                NombreUsuario = o.IdUsuarioNavigation?.NombreCompleto ?? "Tú",
                MontoOfertado = o.MontoOfertado,
                MontoMinimo = o.MontoMinimo,
                TasaCambio = o.TasaCambio,
                TipoOperacion = o.TipoOperacion,
                Estado = o.Estado,
                MonedaEntrega = o.MonedaEntrega,
                MonedaRecibe = o.MonedaRecibe,
                MonedaEntregaCode = o.MonedaEntregaNavigation?.Codigo,
                MonedaRecibeCode = o.MonedaRecibeNavigation?.Codigo,
                FechaPublicacion = o.FechaPublicacion
            });

            return Ok(listaDTO);
        }
        // PUT: api/Oferta/{id}/cancelar
        [HttpPut("{id}/cancelar")]
        [Authorize]
        public async Task<IActionResult> CancelarOferta(long id)
        {
            var idUsuarioClaim = User.FindFirst("IdUsuario")?.Value;
            if (string.IsNullOrEmpty(idUsuarioClaim))
                return Unauthorized("Token inválido.");

            long idUsuario = long.Parse(idUsuarioClaim);

            try
            {
                bool exito = await _repo.CancelarOferta(id, idUsuario);

                if (exito)
                    return Ok(new { mensaje = "Oferta cancelada exitosamente. Tus fondos han sido devueltos a tu saldo disponible." });

                return BadRequest("No se pudo cancelar la oferta.");
            }
            catch (Exception ex)
            {
                return BadRequest($"ERROR AL CANCELAR: {ex.Message}");
            }
        }
    }
}