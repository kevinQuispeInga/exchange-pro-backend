using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UESAN.ExchangePro.CORE.Core.Entities;
using UESAN.ExchangePro.CORE.Core.Interfaces;
using UESAN.ExchangePro.CORE.Infrastructure.Data;

namespace UESAN.ExchangePro.Infrastructure.Repositories
{
    public class DisputaRepository : IDisputaRepository
    {
        private readonly ExchangeProDbContext _context;

        public DisputaRepository(ExchangeProDbContext context)
        {
            _context = context;
        }

        public async Task<bool> AbrirDisputa(Disputas disputa)
        {
            using var dbTransaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // 1. Buscamos la transacción a congelar
                var transaccion = await _context.Transacciones.FindAsync(disputa.IdTransaccion);

                if (transaccion == null)
                    throw new Exception("Transacción no encontrada.");

                // 2. Validar que la transacción pueda entrar en disputa
                if (transaccion.Estado == "COMPLETADO" || transaccion.Estado == "CANCELADO")
                    throw new Exception($"No se puede abrir disputa para una transacción en estado {transaccion.Estado}.");

                // 3. Validar que el usuario que reporta es parte de la transacción (Comprador o Vendedor)
                if (transaccion.CompradorId != disputa.UsuarioReporta && transaccion.VendedorId != disputa.UsuarioReporta)
                    throw new Exception("No tienes permiso para abrir una disputa en esta transacción.");

                // 4. Configurar la disputa
                disputa.Estado = "ABIERTA";
                disputa.FechaCreacion = DateTime.UtcNow;

                // 5. ¡CONGELAR LA TRANSACCIÓN!
                transaccion.Estado = "EN_DISPUTA";

                // 6. Guardar en BD
                await _context.Disputas.AddAsync(disputa);
                _context.Transacciones.Update(transaccion);

                await _context.SaveChangesAsync();
                await dbTransaction.CommitAsync();

                return true;
            }
            catch (Exception ex)
            {
                await dbTransaction.RollbackAsync();
                throw new Exception(ex.InnerException?.Message ?? ex.Message);
            }
        }

        // =========================================================================
        // MÉTODO AGREGADO: Listar las disputas pendientes para la sala del VAR
        // =========================================================================
        public async Task<IEnumerable<Disputas>> GetDisputasPendientes()
        {
            return await _context.Disputas
                                 .Include(d => d.UsuarioReportaNavigation)
                                 .Include(d => d.EvidenciasDisputa)
                                 .Include(d => d.IdTransaccionNavigation)
                                     .ThenInclude(t => t.Comprador)
                                 .Include(d => d.IdTransaccionNavigation)
                                     .ThenInclude(t => t.Vendedor)
                                 .Where(d => d.Estado == "ABIERTA")
                                 .ToListAsync();
        }

        public async Task<bool> ResolverDisputa(long idDisputa, long idAdmin, string decision, string observacion)
        {
            using var dbTransaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // 1. Buscamos la disputa
                var disputa = await _context.Disputas.FindAsync(idDisputa);
                if (disputa == null) throw new Exception("Disputa no encontrada.");
                if (disputa.Estado != "ABIERTA") throw new Exception("Esta disputa ya fue resuelta.");

                // 2. Traemos la Transacción y la Oferta asociadas
                var transaccion = await _context.Transacciones.FindAsync(disputa.IdTransaccion);
                var oferta = await _context.Ofertas.FindAsync(transaccion.IdOferta);

                // 3. Guardamos el fallo del Administrador
                var resolucion = new ResolucionesDisputa
                {
                    IdDisputa = idDisputa,
                    AdministradorId = idAdmin,
                    Decision = decision.ToUpper(),
                    Observacion = observacion,
                    FechaResolucion = DateTime.UtcNow
                };
                await _context.ResolucionesDisputa.AddAsync(resolucion);

                // 4. Actualizamos el estado de la disputa
                disputa.Estado = "RESUELTA";
                _context.Disputas.Update(disputa);

                // 5. ¡EJECUTAMOS LA SENTENCIA!
                if (decision.ToUpper() == "A_FAVOR_COMPRADOR")
                {
                    // El comprobante era real. Forzamos la liberación del dinero.
                    int idMoneda = oferta.MonedaEntrega;

                    var walletVendedor = await _context.Wallets.Include(w => w.WalletSaldos).FirstOrDefaultAsync(w => w.IdUsuario == transaccion.VendedorId);
                    var walletComprador = await _context.Wallets.Include(w => w.WalletSaldos).FirstOrDefaultAsync(w => w.IdUsuario == transaccion.CompradorId);

                    var saldoVendedor = walletVendedor.WalletSaldos.FirstOrDefault(s => s.IdMoneda == idMoneda);
                    var saldoComprador = walletComprador.WalletSaldos.FirstOrDefault(s => s.IdMoneda == idMoneda);

                    if (saldoComprador == null)
                    {
                        saldoComprador = new WalletSaldos { IdMoneda = idMoneda, SaldoDisponible = 0, SaldoRetenido = 0 };
                        walletComprador.WalletSaldos.Add(saldoComprador);
                    }

                    saldoVendedor.SaldoRetenido -= transaccion.MontoOperacion;
                    saldoComprador.SaldoDisponible += transaccion.MontoOperacion;

                    // CORREGIDO: Devolver el saldo restante de la oferta (si es parcial) al disponible del vendedor
                    decimal montoOperacion = transaccion.MontoOperacion ?? 0;
                    decimal montoOfertado = oferta.MontoOfertado;
                    if (montoOfertado > montoOperacion)
                    {
                        decimal restante = montoOfertado - montoOperacion;
                        if ((saldoVendedor.SaldoRetenido ?? 0) >= restante)
                        {
                            saldoVendedor.SaldoRetenido -= restante;
                            saldoVendedor.SaldoDisponible = (saldoVendedor.SaldoDisponible ?? 0) + restante;
                        }
                    }

                    transaccion.Estado = "COMPLETADO";
                    oferta.Estado = "FINALIZADA";

                    _context.Wallets.Update(walletVendedor);
                    _context.Wallets.Update(walletComprador);
                }
                else if (decision.ToUpper() == "A_FAVOR_VENDEDOR")
                {
                    // El comprobante era falso o el dinero no llegó. 
                    // Anulamos la venta y la oferta vuelve a la cancha.
                    transaccion.Estado = "CANCELADO";
                    oferta.Estado = "ACTIVA";
                }
                else
                {
                    throw new Exception("Decisión inválida. Usa 'A_FAVOR_COMPRADOR' o 'A_FAVOR_VENDEDOR'.");
                }

                _context.Transacciones.Update(transaccion);
                _context.Ofertas.Update(oferta);

                await _context.SaveChangesAsync();
                await dbTransaction.CommitAsync();

                return true;
            }
            catch (Exception ex)
            {
                await dbTransaction.RollbackAsync();
                throw new Exception(ex.InnerException?.Message ?? ex.Message);
            }
        }

        public async Task<Disputas?> GetById(long idDisputa)
        {
            return await _context.Disputas.FindAsync(idDisputa);
        }

        public async Task<bool> InsertEvidencia(EvidenciasDisputa evidencia)
        {
            await _context.EvidenciasDisputa.AddAsync(evidencia);
            return await _context.SaveChangesAsync() > 0;
        }
    }
}