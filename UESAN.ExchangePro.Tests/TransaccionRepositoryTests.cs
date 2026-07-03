using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using UESAN.ExchangePro.CORE.Core.Entities;
using UESAN.ExchangePro.CORE.Infrastructure.Data;
using UESAN.ExchangePro.Infrastructure.Repositories;
using Xunit;

namespace UESAN.ExchangePro.Tests
{
    public class TransaccionRepositoryTests
    {
        private ExchangeProDbContext GetInMemoryDbContext(string dbName)
        {
            var options = new DbContextOptionsBuilder<ExchangeProDbContext>()
                .UseInMemoryDatabase(databaseName: dbName)
                // Ignorar la advertencia de que la base de datos en memoria no soporta transacciones reales de base de datos
                .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            return new ExchangeProDbContext(options);
        }

        private async Task SeedReferentialData(ExchangeProDbContext context)
        {
            if (!context.Monedas.Any())
            {
                context.Monedas.AddRange(
                    new Monedas { IdMoneda = 1, Codigo = "PEN", Nombre = "Sol" },
                    new Monedas { IdMoneda = 2, Codigo = "USD", Nombre = "Dolar" }
                );
            }

            if (!context.Roles.Any())
            {
                context.Roles.AddRange(
                    new Roles { IdRol = 1, Nombre = "USER", Descripcion = "Usuario normal" }
                );
            }

            if (!context.MetodosPago.Any())
            {
                context.MetodosPago.AddRange(
                    new MetodosPago { IdMetodoPago = 4, Nombre = "WALLET_INTERNA" }
                );
            }

            await context.SaveChangesAsync();
        }

        [Fact]
        public async Task LiberarFondos_TransaccionParcial_DeberiaMoverEscrowYReembolsarDiferencia()
        {
            // Arrange
            using var context = GetInMemoryDbContext(Guid.NewGuid().ToString());
            await SeedReferentialData(context);

            long compradorId = 10;
            long vendedorId = 20;

            // 1. Crear usuarios
            var comprador = new Usuarios { IdUsuario = compradorId, IdRol = 1, Nombres = "Juan", Apellidos = "Comprador", Correo = "juan@test.com", Telefono = "123", DocumentoIdentidad = "111", PasswordHash = "hash" };
            var vendedor = new Usuarios { IdUsuario = vendedorId, IdRol = 1, Nombres = "Maria", Apellidos = "Vendedor", Correo = "maria@test.com", Telefono = "456", DocumentoIdentidad = "222", PasswordHash = "hash" };
            context.Usuarios.AddRange(comprador, vendedor);

            // 2. Crear wallets
            var walletComprador = new Wallets { IdWallet = 100, IdUsuario = compradorId, FechaCreacion = DateTime.UtcNow };
            var walletVendedor = new Wallets { IdWallet = 200, IdUsuario = vendedorId, FechaCreacion = DateTime.UtcNow };
            context.Wallets.AddRange(walletComprador, walletVendedor);

            // 3. Crear saldos (vendedor vende USD = IdMoneda 2, se le retiene 100 USD en escrow)
            // Saldo disponible vendedor: 0 USD, Saldo retenido: 100 USD
            var saldoVendedorUSD = new WalletSaldos { IdWallet = 200, IdMoneda = 2, SaldoDisponible = 0, SaldoRetenido = 100 };
            // Comprador tiene 0 USD
            var saldoCompradorUSD = new WalletSaldos { IdWallet = 100, IdMoneda = 2, SaldoDisponible = 0, SaldoRetenido = 0 };
            context.WalletSaldos.AddRange(saldoVendedorUSD, saldoCompradorUSD);

            // 4. Crear Oferta (MonedaEntrega = 2/USD, MonedaRecibe = 1/PEN, MontoOfertado = 100)
            var oferta = new Ofertas
            {
                IdOferta = 300,
                IdUsuario = vendedorId,
                TipoOperacion = "VENTA",
                MonedaEntrega = 2,
                MonedaRecibe = 1,
                MontoOfertado = 100,
                MontoMinimo = 20,
                TasaCambio = 3.5m,
                Estado = "EN_PROCESO"
            };
            context.Ofertas.Add(oferta);

            // 5. Crear Transacción (MontoOperacion = 60 USD, parcial de la oferta de 100 USD)
            var transaccion = new Transacciones
            {
                IdTransaccion = 500,
                Codigo = "TRX-TEST-123",
                IdOferta = 300,
                CompradorId = compradorId,
                VendedorId = vendedorId,
                MontoOperacion = 60,
                Estado = "PAGADO", // Listo para liberar
                IdMetodoPago = 4
            };
            context.Transacciones.Add(transaccion);
            await context.SaveChangesAsync();

            var repository = new TransaccionRepository(context);

            // Act
            bool resultado = await repository.LiberarFondos(500, vendedorId);

            // Assert
            Assert.True(resultado);

            var trxDb = await context.Transacciones.FindAsync(500L);
            var ofertaDb = await context.Ofertas.FindAsync(300L);
            var saldoVendedorDb = await context.WalletSaldos.FirstAsync(s => s.IdWallet == 200 && s.IdMoneda == 2);
            var saldoCompradorDb = await context.WalletSaldos.FirstAsync(s => s.IdWallet == 100 && s.IdMoneda == 2);

            // Transacción debe marcarse como COMPLETADA
            Assert.Equal("COMPLETADO", trxDb.Estado);
            // Oferta asociada finaliza
            Assert.Equal("FINALIZADA", ofertaDb.Estado);

            // Al comprador se le entregan los 60 USD de la operación
            Assert.Equal(60, saldoCompradorDb.SaldoDisponible);

            // Del vendedor:
            // - SaldoRetenido debe ser 0 (se descuentan los 60 de la venta + 40 del reembolso)
            Assert.Equal(0, saldoVendedorDb.SaldoRetenido);
            // - SaldoDisponible debe ser 40 (el reembolso del sobrante de la oferta)
            Assert.Equal(40, saldoVendedorDb.SaldoDisponible);
        }

        [Fact]
        public async Task PagarConWallet_DeberiaMoverFondosDePagoYLiberarEscrowCorrectamente()
        {
            // Arrange
            using var context = GetInMemoryDbContext(Guid.NewGuid().ToString());
            await SeedReferentialData(context);

            long compradorId = 10;
            long vendedorId = 20;

            // 1. Crear usuarios
            var comprador = new Usuarios { IdUsuario = compradorId, IdRol = 1, Nombres = "Juan", Apellidos = "Comprador", Correo = "juan@test.com", Telefono = "123", DocumentoIdentidad = "111", PasswordHash = "hash" };
            var vendedor = new Usuarios { IdUsuario = vendedorId, IdRol = 1, Nombres = "Maria", Apellidos = "Vendedor", Correo = "maria@test.com", Telefono = "456", DocumentoIdentidad = "222", PasswordHash = "hash" };
            context.Usuarios.AddRange(comprador, vendedor);

            // 2. Crear wallets
            var walletComprador = new Wallets { IdWallet = 100, IdUsuario = compradorId, FechaCreacion = DateTime.UtcNow };
            var walletVendedor = new Wallets { IdWallet = 200, IdUsuario = vendedorId, FechaCreacion = DateTime.UtcNow };
            context.Wallets.AddRange(walletComprador, walletVendedor);

            // 3. Crear saldos
            // Comprador tiene saldo en Soles (MonedaRecibe = 1) para pagar. Requiere 60 USD * 3.5 = 210 PEN. Le damos 300 PEN.
            var saldoCompradorPEN = new WalletSaldos { IdWallet = 100, IdMoneda = 1, SaldoDisponible = 300, SaldoRetenido = 0 };
            var saldoCompradorUSD = new WalletSaldos { IdWallet = 100, IdMoneda = 2, SaldoDisponible = 0, SaldoRetenido = 0 };

            // Vendedor tiene 100 USD en escrow (MonedaEntrega = 2) y 0 PEN
            var saldoVendedorUSD = new WalletSaldos { IdWallet = 200, IdMoneda = 2, SaldoDisponible = 0, SaldoRetenido = 100 };
            var saldoVendedorPEN = new WalletSaldos { IdWallet = 200, IdMoneda = 1, SaldoDisponible = 0, SaldoRetenido = 0 };
            context.WalletSaldos.AddRange(saldoCompradorPEN, saldoCompradorUSD, saldoVendedorUSD, saldoVendedorPEN);

            // 4. Crear Oferta (MonedaEntrega = 2/USD, MonedaRecibe = 1/PEN, MontoOfertado = 100)
            var oferta = new Ofertas
            {
                IdOferta = 300,
                IdUsuario = vendedorId,
                TipoOperacion = "VENTA",
                MonedaEntrega = 2,
                MonedaRecibe = 1,
                MontoOfertado = 100,
                MontoMinimo = 20,
                TasaCambio = 3.5m,
                Estado = "PENDIENTE"
            };
            context.Ofertas.Add(oferta);

            // 5. Crear Transacción (MontoOperacion = 60 USD, IdMetodoPago = 4 (Wallet Interna))
            var transaccion = new Transacciones
            {
                IdTransaccion = 500,
                Codigo = "TRX-TEST-456",
                IdOferta = 300,
                CompradorId = compradorId,
                VendedorId = vendedorId,
                MontoOperacion = 60,
                Estado = "PENDIENTE",
                IdMetodoPago = 4
            };
            context.Transacciones.Add(transaccion);
            await context.SaveChangesAsync();

            var repository = new TransaccionRepository(context);

            // Act
            bool resultado = await repository.PagarConWallet(500, compradorId);

            // Assert
            Assert.True(resultado);

            var trxDb = await context.Transacciones.FindAsync(500L);
            var ofertaDb = await context.Ofertas.FindAsync(300L);
            
            var compradorUSD = await context.WalletSaldos.FirstAsync(s => s.IdWallet == 100 && s.IdMoneda == 2);
            var compradorPEN = await context.WalletSaldos.FirstAsync(s => s.IdWallet == 100 && s.IdMoneda == 1);
            var vendedorUSD = await context.WalletSaldos.FirstAsync(s => s.IdWallet == 200 && s.IdMoneda == 2);
            var vendedorPEN = await context.WalletSaldos.FirstAsync(s => s.IdWallet == 200 && s.IdMoneda == 1);

            // 1. Estados finales
            Assert.Equal("COMPLETADO", trxDb.Estado);
            Assert.Equal("FINALIZADA", ofertaDb.Estado);

            // 2. Comprador pagó 210 PEN y recibió 60 USD
            Assert.Equal(90, compradorPEN.SaldoDisponible); // 300 - 210
            Assert.Equal(60, compradorUSD.SaldoDisponible);

            // 3. Vendedor recibió 210 PEN, su escrow de 100 USD se liberó (60 a comprador + 40 reembolsados)
            Assert.Equal(210, vendedorPEN.SaldoDisponible);
            Assert.Equal(0, vendedorUSD.SaldoRetenido);
            Assert.Equal(40, vendedorUSD.SaldoDisponible); // Reembolso de 40 USD sobrantes
        }
    }
}
