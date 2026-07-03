using Microsoft.EntityFrameworkCore;
using UESAN.ExchangePro.CORE.Core.Entities;
using UESAN.ExchangePro.CORE.Core.Interfaces;
using UESAN.ExchangePro.CORE.Infrastructure.Data;

namespace UESAN.ExchangePro.Infrastructure.Repositories
{
    public class TransaccionRepository : ITransaccionRepository
    {
        private readonly ExchangeProDbContext _context;

        public TransaccionRepository(ExchangeProDbContext context)
        {
            _context = context;
        }

        // 1. Crear la transacción (con protección atómica y captura de errores)
        public async Task<bool> CrearTransaccion(Transacciones transaccion, Ofertas oferta)
        {
            using var dbTransaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // Agregamos la nueva transacción
                await _context.Transacciones.AddAsync(transaccion);

                // Cambiamos el estado de la oferta a EN_PROCESO
                oferta.Estado = "EN_PROCESO";
                _context.Ofertas.Update(oferta);

                // Guardamos los cambios
                await _context.SaveChangesAsync();

                // Confirmamos la transacción atómica
                await dbTransaction.CommitAsync();
                return true;
            }
            catch (Exception ex)
            {
                // Revertimos cambios si algo falla y lanzamos el error para verlo en Postman
                await dbTransaction.RollbackAsync();
                throw new Exception(ex.InnerException?.Message ?? ex.Message);
            }
        }

        // 2. Obtener todas las transacciones de un usuario (comprador o vendedor)
        public async Task<IEnumerable<Transacciones>> GetByUsuario(long idUsuario)
        {
            return await _context.Transacciones
                .Include(t => t.Comprador)
                .Include(t => t.Vendedor)
                .Include(t => t.IdOfertaNavigation)
                    .ThenInclude(o => o.MonedaEntregaNavigation)
                .Include(t => t.IdOfertaNavigation)
                    .ThenInclude(o => o.MonedaRecibeNavigation)
                .Where(t => t.CompradorId == idUsuario || t.VendedorId == idUsuario)
                .ToListAsync();
        }

        // 3. Actualizar el estado de una transacción (ej. de PENDIENTE a PAGADO)
        public async Task<bool> ActualizarEstado(long idTransaccion, string nuevoEstado)
        {
            var transaccion = await _context.Transacciones.FindAsync(idTransaccion);

            if (transaccion == null) return false;

            transaccion.Estado = nuevoEstado;

            _context.Transacciones.Update(transaccion);
            return await _context.SaveChangesAsync() > 0;
        }

        // 4. Obtener una transacción específica por su ID
        // (Nota: Si tu ITransaccionRepository.cs tiene definido este método con 'int', 
        // cambia este 'long' por 'int' para que coincidan perfectamente).
        public async Task<Transacciones?> GetById(long idTransaccion)
        {
            return await _context.Transacciones.FindAsync(idTransaccion);
        }

        // 5. Liberar los fondos (Transferencia P2P final)
        public async Task<bool> LiberarFondos(long idTransaccion, long idVendedor)
        {
            using var dbTransaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // 1. Validar la transacción
                var transaccion = await _context.Transacciones.FindAsync(idTransaccion);
                if (transaccion == null) throw new Exception("Transacción no encontrada.");
                if (transaccion.VendedorId != idVendedor) throw new Exception("Solo el vendedor puede liberar los fondos.");
                if (transaccion.Estado != "PAGADO") throw new Exception("El comprador aún no ha marcado esto como PAGADO.");

                // 2. Obtener la oferta para saber qué moneda estamos moviendo
                var oferta = await _context.Ofertas.FindAsync(transaccion.IdOferta);
                if (oferta == null) throw new Exception("Oferta original no encontrada.");

                // Identificar qué moneda se congeló en el escrow según el tipo de operación:
                // VENTA: Se retiene MonedaEntrega (el vendedor entrega divisas)
                // COMPRA: Se retiene MonedaRecibe (el vendedor paga moneda nacional y compra divisas)
                int idMonedaEscrow = oferta.MonedaEntrega;
                decimal montoOpATransferir = transaccion.MontoOperacion ?? 0;
                decimal montoOfertadoTotal = oferta.MontoOfertado;

                if (oferta.TipoOperacion.ToUpper() == "COMPRA")
                {
                    idMonedaEscrow = oferta.MonedaRecibe;
                    montoOpATransferir = (transaccion.MontoOperacion ?? 0) * oferta.TasaCambio;
                    montoOfertadoTotal = oferta.MontoOfertado * oferta.TasaCambio;
                }

                // 3. Obtener Wallets (incluyendo sus saldos)
                var walletVendedor = await _context.Wallets
                    .Include(w => w.WalletSaldos)
                    .FirstOrDefaultAsync(w => w.IdUsuario == idVendedor);

                var walletComprador = await _context.Wallets
                    .Include(w => w.WalletSaldos)
                    .FirstOrDefaultAsync(w => w.IdUsuario == transaccion.CompradorId);

                if (walletVendedor == null || walletComprador == null)
                    throw new Exception("Falta la wallet del comprador o del vendedor en el sistema.");

                // 4. Mover el dinero
                var saldoVendedor = walletVendedor.WalletSaldos.FirstOrDefault(s => s.IdMoneda == idMonedaEscrow);
                if (saldoVendedor == null || (saldoVendedor.SaldoRetenido ?? 0) < montoOpATransferir)
                    throw new Exception("El vendedor no tiene saldo retenido suficiente en esta moneda para liberar.");

                var saldoComprador = walletComprador.WalletSaldos.FirstOrDefault(s => s.IdMoneda == idMonedaEscrow);
                if (saldoComprador == null)
                {
                    // Si el comprador no tiene billetera para esta moneda, se la creamos en el momento
                    saldoComprador = new WalletSaldos { IdMoneda = idMonedaEscrow, SaldoDisponible = 0, SaldoRetenido = 0 };
                    walletComprador.WalletSaldos.Add(saldoComprador);
                }

                // Liberar el dinero desde el retenido del vendedor al disponible del comprador
                saldoVendedor.SaldoRetenido -= montoOpATransferir;
                saldoComprador.SaldoDisponible += montoOpATransferir;

                // Devolver el saldo restante de la oferta (si es parcial) al disponible del vendedor
                if (montoOfertadoTotal > montoOpATransferir)
                {
                    decimal restante = montoOfertadoTotal - montoOpATransferir;
                    if ((saldoVendedor.SaldoRetenido ?? 0) >= restante)
                    {
                        saldoVendedor.SaldoRetenido -= restante;
                        saldoVendedor.SaldoDisponible = (saldoVendedor.SaldoDisponible ?? 0) + restante;
                    }
                }

                // 5. Actualizar los estados finales
                transaccion.Estado = "COMPLETADO";
                oferta.Estado = "FINALIZADA";

                // 6. Guardar todo
                _context.Transacciones.Update(transaccion);
                _context.Ofertas.Update(oferta);
                _context.Wallets.Update(walletVendedor);
                _context.Wallets.Update(walletComprador);

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
        // 6. Cancelar Transacción y devolver la Oferta al mercado
        public async Task<bool> CancelarTransaccion(long idTransaccion, long idUsuario)
        {
            using var dbTransaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // 1. Buscamos la transacción
                var transaccion = await _context.Transacciones.FindAsync(idTransaccion);
                if (transaccion == null) throw new Exception("Transacción no encontrada.");

                // 2. Validaciones de seguridad y estado
                if (transaccion.CompradorId != idUsuario && transaccion.VendedorId != idUsuario)
                    throw new Exception("No tienes permiso para cancelar esta transacción.");

                if (transaccion.Estado != "PENDIENTE")
                    throw new Exception("Solo se pueden cancelar transacciones que estén PENDIENTES.");

                // 3. Buscamos la oferta asociada
                var oferta = await _context.Ofertas.FindAsync(transaccion.IdOferta);
                if (oferta == null) throw new Exception("Oferta original no encontrada.");

                // 4. Aplicamos los cambios (El VAR anula la jugada)
                transaccion.Estado = "CANCELADA";

                // La oferta vuelve a la cancha
                oferta.Estado = "ACTIVA";

                // 5. Guardamos en base de datos
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
        // 7. Obtener la transacción incluyendo los datos del banco
        public async Task<Transacciones?> GetTransaccionConMetodoPago(long idTransaccion)
        {
            return await _context.Transacciones
                .Include(t => t.IdMetodoPagoNavigation)
                .Include(t => t.IdOfertaNavigation)
                    .ThenInclude(o => o.MonedaEntregaNavigation)
                .Include(t => t.IdOfertaNavigation)
                    .ThenInclude(o => o.MonedaRecibeNavigation)
                .FirstOrDefaultAsync(t => t.IdTransaccion == idTransaccion);
        }
        // 8. Obtener los datos de pago reales del vendedor (fusiona todos sus registros)
        public async Task<DatosPagoUsuario?> GetDatosPagoVendedor(long idVendedor)
        {
            var registros = await _context.DatosPagoUsuario
                .Where(d => d.IdUsuario == idVendedor)
                .ToListAsync();
            if (!registros.Any()) return null;
            return new DatosPagoUsuario
            {
                IdUsuario = idVendedor,
                Yape = registros.Select(r => r.Yape).FirstOrDefault(v => !string.IsNullOrEmpty(v)),
                Plin = registros.Select(r => r.Plin).FirstOrDefault(v => !string.IsNullOrEmpty(v)),
                NumeroCuenta = registros.Select(r => r.NumeroCuenta).FirstOrDefault(v => !string.IsNullOrEmpty(v)),
                Cci = registros.Select(r => r.Cci).FirstOrDefault(v => !string.IsNullOrEmpty(v))
            };
        }
        // 9. Marcar la transacción como PAGADA guardando el voucher
        public async Task<bool> MarcarComoPagado(long idTransaccion, long idComprador, string rutaComprobante)
        {
            var transaccion = await _context.Transacciones.FindAsync(idTransaccion);

            if (transaccion == null) throw new Exception("Transacción no encontrada.");
            if (transaccion.CompradorId != idComprador) throw new Exception("Solo el comprador puede marcar esto como pagado.");
            if (transaccion.Estado != "PENDIENTE") throw new Exception("La transacción no está en estado PENDIENTE.");

            // Guardamos la ruta de la imagen y cambiamos el estado
            transaccion.RutaComprobante = rutaComprobante;
            transaccion.Estado = "PAGADO";

            _context.Transacciones.Update(transaccion);
            await _context.SaveChangesAsync();

            return true;
        }

        public async Task<bool> PagarConWallet(long idTransaccion, long idComprador)
        {
            using var dbTransaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var transaccion = await _context.Transacciones.FindAsync(idTransaccion);
                if (transaccion == null) throw new Exception("Transacción no encontrada.");
                if (transaccion.CompradorId != idComprador) throw new Exception("Solo el comprador puede pagar con wallet.");
                if (transaccion.Estado != "PENDIENTE") throw new Exception("La transacción no está en estado PENDIENTE.");
                if (transaccion.IdMetodoPago != 4) throw new Exception("Esta transacción no usa Wallet Interna como método de pago.");

                var oferta = await _context.Ofertas.FindAsync(transaccion.IdOferta);
                if (oferta == null) throw new Exception("Oferta original no encontrada.");

                decimal montoOp = transaccion.MontoOperacion ?? 0;
                int monedaEntrega = oferta.MonedaEntrega;
                int monedaRecibe = oferta.MonedaRecibe;
                decimal tasaCambio = oferta.TasaCambio;

                // Calcular cuánto debe pagar el comprador al vendedor en MonedaRecibe
                decimal montoAPagar = montoOp;
                if (tasaCambio > 0 && monedaEntrega != monedaRecibe)
                {
                    var eCode = await _context.Monedas.Where(m => m.IdMoneda == monedaEntrega).Select(m => m.Codigo).FirstOrDefaultAsync();
                    var rCode = await _context.Monedas.Where(m => m.IdMoneda == monedaRecibe).Select(m => m.Codigo).FirstOrDefaultAsync();
                    
                    if (eCode == "PEN" && rCode == "USD")
                    {
                        montoAPagar = Math.Round(montoOp / tasaCambio, 2);
                    }
                    else if (eCode == "USD" && rCode == "PEN")
                    {
                        montoAPagar = Math.Round(montoOp * tasaCambio, 2);
                    }
                }

                var walletComprador = await _context.Wallets
                    .Include(w => w.WalletSaldos)
                    .FirstOrDefaultAsync(w => w.IdUsuario == idComprador);
                if (walletComprador == null) throw new Exception("El comprador no tiene billetera.");

                var walletVendedor = await _context.Wallets
                    .Include(w => w.WalletSaldos)
                    .FirstOrDefaultAsync(w => w.IdUsuario == transaccion.VendedorId);
                if (walletVendedor == null) throw new Exception("El vendedor no tiene billetera.");

                // 1. EL COMPRADOR PAGA AL VENDEDOR (La contraparte del Escrow)
                // VENTA: Comprador paga en MonedaRecibe (el valor de la compra).
                // COMPRA: Comprador paga en MonedaEntrega (entrega divisas al vendedor).
                int monedaPagoComprador = oferta.TipoOperacion.ToUpper() == "COMPRA" ? monedaEntrega : monedaRecibe;
                decimal montoPagoComprador = oferta.TipoOperacion.ToUpper() == "COMPRA" ? montoOp : montoAPagar;

                var saldoCompradorPago = walletComprador.WalletSaldos.FirstOrDefault(s => s.IdMoneda == monedaPagoComprador);
                if (saldoCompradorPago == null || (saldoCompradorPago.SaldoDisponible ?? 0) < montoPagoComprador)
                    throw new Exception($"Saldo disponible insuficiente en la billetera del comprador para pagar {montoPagoComprador} en la moneda correspondiente.");

                saldoCompradorPago.SaldoDisponible -= montoPagoComprador;

                var saldoVendedorRecibe = walletVendedor.WalletSaldos.FirstOrDefault(s => s.IdMoneda == monedaPagoComprador);
                if (saldoVendedorRecibe == null)
                {
                    saldoVendedorRecibe = new WalletSaldos { IdMoneda = monedaPagoComprador, SaldoDisponible = 0, SaldoRetenido = 0 };
                    walletVendedor.WalletSaldos.Add(saldoVendedorRecibe);
                }
                saldoVendedorRecibe.SaldoDisponible = (saldoVendedorRecibe.SaldoDisponible ?? 0) + montoPagoComprador;

                // 2. SE LIBERA EL ESCROW DEL VENDEDOR AL COMPRADOR
                // VENTA: Se libera MonedaEntrega (el vendedor entrega divisas congeladas).
                // COMPRA: Se libera MonedaRecibe (el vendedor entrega la moneda de pago que estaba congelada).
                int monedaEscrowVendedor = oferta.TipoOperacion.ToUpper() == "COMPRA" ? monedaRecibe : monedaEntrega;
                decimal montoEscrowLiberar = oferta.TipoOperacion.ToUpper() == "COMPRA" ? montoAPagar : montoOp;
                decimal montoOfertadoTotal = oferta.TipoOperacion.ToUpper() == "COMPRA" ? (oferta.MontoOfertado * oferta.TasaCambio) : oferta.MontoOfertado;

                var saldoVendedorEntrega = walletVendedor.WalletSaldos.FirstOrDefault(s => s.IdMoneda == monedaEscrowVendedor);
                if (saldoVendedorEntrega == null || (saldoVendedorEntrega.SaldoRetenido ?? 0) < montoEscrowLiberar)
                    throw new Exception("El vendedor no tiene saldo retenido suficiente en la moneda de garantía para liberar.");

                saldoVendedorEntrega.SaldoRetenido -= montoEscrowLiberar;

                var saldoCompradorEntrega = walletComprador.WalletSaldos.FirstOrDefault(s => s.IdMoneda == monedaEscrowVendedor);
                if (saldoCompradorEntrega == null)
                {
                    saldoCompradorEntrega = new WalletSaldos { IdMoneda = monedaEscrowVendedor, SaldoDisponible = 0, SaldoRetenido = 0 };
                    walletComprador.WalletSaldos.Add(saldoCompradorEntrega);
                }
                saldoCompradorEntrega.SaldoDisponible = (saldoCompradorEntrega.SaldoDisponible ?? 0) + montoEscrowLiberar;

                // 3. DEVOLVER EL SALDO RESTANTE DE LA OFERTA (Si es parcial) AL SALDO DISPONIBLE DEL VENDEDOR
                if (montoOfertadoTotal > montoEscrowLiberar)
                {
                    decimal restante = montoOfertadoTotal - montoEscrowLiberar;
                    if ((saldoVendedorEntrega.SaldoRetenido ?? 0) >= restante)
                    {
                        saldoVendedorEntrega.SaldoRetenido -= restante;
                        saldoVendedorEntrega.SaldoDisponible = (saldoVendedorEntrega.SaldoDisponible ?? 0) + restante;
                    }
                }

                // 4. REGISTRAR LOS MOVIMIENTOS EN LA WALLET
                var movComprador = new MovimientosWallet
                {
                    IdWallet = walletComprador.IdWallet,
                    IdMoneda = monedaPagoComprador,
                    TipoOperacion = "TRANSFERENCIA_SALIDA",
                    Monto = montoPagoComprador,
                    Resultado = "EXITOSO",
                    ReferenciaTipo = "TRANSACCION",
                    ReferenciaId = idTransaccion,
                    FechaMovimiento = DateTime.UtcNow
                };
                _context.MovimientosWallet.Add(movComprador);

                var movVendedor = new MovimientosWallet
                {
                    IdWallet = walletVendedor.IdWallet,
                    IdMoneda = monedaPagoComprador,
                    TipoOperacion = "TRANSFERENCIA_ENTRADA",
                    Monto = montoPagoComprador,
                    Resultado = "EXITOSO",
                    ReferenciaTipo = "TRANSACCION",
                    ReferenciaId = idTransaccion,
                    FechaMovimiento = DateTime.UtcNow
                };
                _context.MovimientosWallet.Add(movVendedor);

                // 5. ACTUALIZAR LOS ESTADOS FINALES
                transaccion.Estado = "COMPLETADO";
                transaccion.FechaFin = DateTime.UtcNow;
                oferta.Estado = "FINALIZADA";

                _context.Transacciones.Update(transaccion);
                _context.Ofertas.Update(oferta);
                _context.Wallets.Update(walletComprador);
                _context.Wallets.Update(walletVendedor);

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
    }
}