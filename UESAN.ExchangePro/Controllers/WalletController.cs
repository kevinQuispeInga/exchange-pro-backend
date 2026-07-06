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
    public class WalletController : ControllerBase
    {
        private readonly IWalletRepository _walletRepository;
        private readonly IRecargaRepository _recargaRepository;

        public WalletController(IWalletRepository walletRepository, IRecargaRepository recargaRepository)
        {
            _walletRepository = walletRepository;
            _recargaRepository = recargaRepository;
        }

        [HttpGet("saldo")]
        public async Task<IActionResult> GetSaldo()
        {
            var idUsuario = long.Parse(User.FindFirst("IdUsuario")?.Value ?? "0");
            var wallet = await _walletRepository.GetByUsuarioId(idUsuario);

            if (wallet == null) return NotFound("Wallet no encontrada.");

            var saldos = wallet.WalletSaldos.Select(s => new {
                idMoneda = s.IdMoneda,
                saldoDisponible = s.SaldoDisponible,
                saldoRetenido = s.SaldoRetenido
            });

            return Ok(new { idWallet = wallet.IdWallet, saldos = saldos });
        }

        [HttpPost("recargar")]
        public async Task<IActionResult> Recargar([FromBody] RecargaDTO dto)
        {
            if (dto.Monto <= 0) return BadRequest("El monto debe ser mayor a cero.");

            if (!string.IsNullOrEmpty(dto.NumeroReferencia))
            {
                bool existe = await _recargaRepository.ExisteReferencia(dto.NumeroReferencia);
                if (existe) return BadRequest(new { mensaje = "El número de referencia/operación ya ha sido utilizado." });
            }

            var idUsuario = long.Parse(User.FindFirst("IdUsuario")?.Value ?? "0");
            var wallet = await _walletRepository.GetByUsuarioId(idUsuario);

            if (wallet == null) return NotFound("Wallet no encontrada.");

            var saldoMoneda = wallet.WalletSaldos.FirstOrDefault(s => s.IdMoneda == dto.IdMoneda);

            if (saldoMoneda != null)
            {
                saldoMoneda.SaldoDisponible = (saldoMoneda.SaldoDisponible ?? 0) + dto.Monto;
            }
            else
            {
                wallet.WalletSaldos.Add(new WalletSaldos
                {
                    IdMoneda = dto.IdMoneda,
                    SaldoDisponible = dto.Monto,
                    SaldoRetenido = 0
                });
            }

            var recarga = new Recargas
            {
                IdUsuario = idUsuario,
                IdMoneda = dto.IdMoneda,
                Monto = dto.Monto,
                MetodoPago = dto.MetodoPago,
                NumeroReferencia = dto.NumeroReferencia,
                Estado = "COMPLETADA"
            };

            await _recargaRepository.Insert(recarga);

            wallet.MovimientosWallet.Add(new MovimientosWallet
            {
                IdMoneda = dto.IdMoneda,
                TipoOperacion = "RECARGA",
                Monto = dto.Monto,
                Resultado = "EXITOSO",
                ReferenciaTipo = dto.MetodoPago,
                ReferenciaId = recarga.IdRecarga
            });

            bool actualizado = await _walletRepository.Update(wallet);

            if (actualizado)
                return Ok(new { mensaje = "Saldo recargado correctamente." });

            return BadRequest("Error al procesar la recarga.");
        }
    }
}