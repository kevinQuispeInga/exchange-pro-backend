using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UESAN.ExchangePro.CORE.Core.DTOs;
using UESAN.ExchangePro.CORE.Core.Entities;
using UESAN.ExchangePro.CORE.Core.Interfaces;

namespace UESAN.ExchangePro.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class RetiroController : ControllerBase
    {
        private readonly IRetiroRepository _retiroRepository;
        private readonly IWalletRepository _walletRepository;
        private readonly INotificacionesRepository _notificacionesRepository;

        public RetiroController(IRetiroRepository retiroRepository, IWalletRepository walletRepository, INotificacionesRepository notificacionesRepository)
        {
            _retiroRepository = retiroRepository;
            _walletRepository = walletRepository;
            _notificacionesRepository = notificacionesRepository;
        }

        [HttpPost("solicitar")]
        public async Task<IActionResult> SolicitarRetiro([FromBody] SolicitarRetiroDTO dto)
        {
            if (dto.Monto <= 0)
                return BadRequest(new { error = "El monto debe ser mayor a cero." });

            var idUsuario = long.Parse(User.FindFirst("IdUsuario")?.Value ?? "0");
            var wallet = await _walletRepository.GetByUsuarioId(idUsuario);

            if (wallet == null)
                return NotFound(new { error = "Wallet no encontrada." });

            var saldoMoneda = wallet.WalletSaldos.FirstOrDefault(s => s.IdMoneda == dto.IdMoneda);
            if (saldoMoneda == null || (saldoMoneda.SaldoDisponible ?? 0) < dto.Monto)
                return BadRequest(new { error = "Saldo disponible insuficiente." });

            saldoMoneda.SaldoDisponible -= dto.Monto;

            var retiro = new Retiros
            {
                IdUsuario = idUsuario,
                IdMoneda = dto.IdMoneda,
                Monto = dto.Monto,
                MetodoRetiro = dto.MetodoRetiro,
                CuentaDestino = dto.CuentaDestino,
                Titular = dto.Titular,
                Estado = "COMPLETADO",
                FechaRetiro = DateTime.Now
            };

            await _retiroRepository.Insert(retiro);

            wallet.MovimientosWallet.Add(new MovimientosWallet
            {
                IdMoneda = dto.IdMoneda,
                TipoOperacion = "RETIRO",
                Monto = dto.Monto,
                Resultado = "EXITOSO",
                ReferenciaTipo = dto.MetodoRetiro,
                ReferenciaId = retiro.IdRetiro
            });

            await _walletRepository.Update(wallet);

            await _notificacionesRepository.Create(new Notificaciones
            {
                IdUsuario = idUsuario,
                Titulo = "Retiro procesado con éxito",
                Mensaje = $"Se ha retirado {dto.Monto} de tu billetera hacia tu cuenta configurada.",
                Fecha = DateTime.UtcNow,
                Leido = false
            });

            return Ok(new
            {
                mensaje = "Retiro procesado correctamente.",
                idRetiro = retiro.IdRetiro
            });
        }

        [HttpGet("mis-retiros")]
        public async Task<IActionResult> MisRetiros()
        {
            var idUsuario = long.Parse(User.FindFirst("IdUsuario")?.Value ?? "0");
            var retiros = await _retiroRepository.GetByUsuario(idUsuario);

            var result = retiros.Select(r => new RetiroResponseDTO
            {
                IdRetiro = r.IdRetiro,
                MonedaCodigo = r.IdMonedaNavigation?.Codigo,
                Monto = r.Monto,
                Estado = r.Estado,
                FechaRetiro = r.FechaRetiro
            });

            return Ok(result);
        }
    }
}