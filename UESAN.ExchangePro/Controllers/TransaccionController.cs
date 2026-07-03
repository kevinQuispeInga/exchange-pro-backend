using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http; // IMPORTANTE: Necesario para IFormFile
using System;
using System.IO; // IMPORTANTE: Necesario para manejar carpetas y archivos
using System.Linq;
using System.Threading.Tasks;
using UESAN.ExchangePro.CORE.Core.DTOs;
using UESAN.ExchangePro.CORE.Core.Entities;
using UESAN.ExchangePro.CORE.Core.Interfaces;

namespace UESAN.ExchangePro.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize] // Todo el flujo requiere que el usuario esté logueado
    public class TransaccionController : ControllerBase
    {
        private readonly ITransaccionRepository _transRepo;
        private readonly IOfertaRepository _ofertaRepo;
        private readonly INotificacionesRepository _notificacionesRepository;

        public TransaccionController(ITransaccionRepository transRepo, IOfertaRepository ofertaRepo, INotificacionesRepository notificacionesRepository)
        {
            _transRepo = transRepo;
            _ofertaRepo = ofertaRepo;
            _notificacionesRepository = notificacionesRepository;
        }

        // POST: api/Transaccion
        [HttpPost]
        public async Task<IActionResult> IniciarTransaccion([FromBody] CrearTransaccionDTO dto)
        {
            var idCompradorClaim = User.FindFirst("IdUsuario")?.Value;
            if (string.IsNullOrEmpty(idCompradorClaim))
                return Unauthorized("Usuario no identificado.");

            long idComprador = long.Parse(idCompradorClaim);

            // 1. Obtener la oferta
            var oferta = await _ofertaRepo.GetById(dto.IdOferta);

            // 2. Validaciones básicas
            if (oferta == null) return NotFound("Oferta no encontrada.");
            if (oferta.Estado != "ACTIVA") return BadRequest("La oferta no está disponible.");
            if (oferta.IdUsuario == idComprador) return BadRequest("No puedes tomar tu propia oferta.");

            // 2b. Validar que Monto esté entre MontoMinimo y MontoOfertado
            if (dto.Monto < oferta.MontoMinimo)
                return BadRequest($"El monto mínimo para esta oferta es {oferta.MontoMinimo}.");
            if (dto.Monto > oferta.MontoOfertado)
                return BadRequest($"El monto no puede exceder el monto ofertado ({oferta.MontoOfertado}).");

            // 3. Crear el objeto transacción usando el monto proporcionado
            var transaccion = new Transacciones
            {
                Codigo = "TRX-" + Guid.NewGuid().ToString().Substring(0, 8).ToUpper(),
                IdOferta = oferta.IdOferta,
                CompradorId = idComprador,
                VendedorId = oferta.IdUsuario,
                MontoOperacion = dto.Monto,
                Estado = "PENDIENTE",
                FechaInicio = DateTime.UtcNow,
                IdMetodoPago = dto.IdMetodoPago
            };

            // 4. Guardar en base de datos capturando el error real
            try
            {
                bool exito = await _transRepo.CrearTransaccion(transaccion, oferta);
                if (!exito)
                    return BadRequest("No se pudo iniciar la transacción.");

                await _notificacionesRepository.Create(new Notificaciones
                {
                    IdUsuario = oferta.IdUsuario,
                    Titulo = "Nueva transacción en tu oferta",
                    Mensaje = $"Se ha iniciado la transacción {transaccion.Codigo} por {transaccion.MontoOperacion}.",
                    Fecha = DateTime.UtcNow,
                    Leido = false
                });
                return Ok(new { mensaje = "Transacción iniciada correctamente.", idTransaccion = transaccion.IdTransaccion });
            }
            catch (Exception ex)
            {
                return BadRequest($"ERROR DE BASE DE DATOS: {ex.Message}");
            }
        }

        // GET: api/Transaccion/mis-operaciones
        [HttpGet("mis-operaciones")]
        public async Task<IActionResult> GetMisOperaciones()
        {
            var idUsuario = long.Parse(User.FindFirst("IdUsuario")?.Value ?? "0");

            // Obtenemos las transacciones desde la base de datos
            var transacciones = await _transRepo.GetByUsuario(idUsuario);

            // Mapeamos a DTO para evitar el error 500 de referencias circulares
            var listaDTO = transacciones.Select(t => {
                var oferta = t.IdOfertaNavigation;
                var monedaEntrega = oferta?.MonedaEntregaNavigation?.Codigo;
                var monedaRecibe = oferta?.MonedaRecibeNavigation?.Codigo;
                var compradorNombre = t.Comprador != null ? $"{t.Comprador.Nombres} {t.Comprador.Apellidos}" : "Desconocido";
                var vendedorNombre = t.Vendedor != null ? $"{t.Vendedor.Nombres} {t.Vendedor.Apellidos}" : "Desconocido";
                
                decimal tasaCambio = oferta?.TasaCambio ?? 0m;
                decimal montoOperacion = t.MontoOperacion ?? 0m;
                decimal totalPagar = montoOperacion;
                if (tasaCambio > 0 && !string.IsNullOrEmpty(monedaEntrega) && !string.IsNullOrEmpty(monedaRecibe))
                {
                    var e = monedaEntrega.ToUpper();
                    var r = monedaRecibe.ToUpper();
                    if (e != r)
                    {
                        if (e == "PEN" && r == "USD")
                            totalPagar = Math.Round(montoOperacion / tasaCambio, 2);
                        else if (e == "USD" && r == "PEN")
                            totalPagar = Math.Round(montoOperacion * tasaCambio, 2);
                    }
                }

                return new TransaccionResponseDTO
                {
                    IdTransaccion = t.IdTransaccion,
                    IdOferta = t.IdOferta,
                    CompradorId = t.CompradorId,
                    VendedorId = t.VendedorId,
                    MontoOperacion = t.MontoOperacion,
                    Estado = t.Estado,
                    FechaInicio = t.FechaInicio,
                    Codigo = t.Codigo,
                    RutaComprobante = t.RutaComprobante,
                    MonedaEntregaCode = monedaEntrega,
                    MonedaRecibeCode = monedaRecibe,
                    CompradorNombre = compradorNombre,
                    VendedorNombre = vendedorNombre,
                    TotalPagar = totalPagar
                };
            });

            return Ok(listaDTO);
        }

        // PUT: api/Transaccion/estado
        [HttpPut("estado")]
        public async Task<IActionResult> ActualizarEstado([FromBody] ActualizarEstadoDTO dto)
        {
            // 1. Validar que el estado sea uno de los permitidos
            var estadosValidos = new[] { "PAGADO", "COMPLETADO", "CANCELADO" };
            if (!estadosValidos.Contains(dto.NuevoEstado.ToUpper()))
                return BadRequest("Estado no válido.");

            // 2. Aquí llamaremos al repositorio para hacer el cambio en la BD
            bool exito = await _transRepo.ActualizarEstado(dto.IdTransaccion, dto.NuevoEstado.ToUpper());

            if (exito)
            {
                var nuevoEstado = dto.NuevoEstado.ToUpper();
                var transaccion = await _transRepo.GetById(dto.IdTransaccion);
                if (transaccion != null)
                {
                    if (nuevoEstado == "PAGADO")
                    {
                        await _notificacionesRepository.Create(new Notificaciones
                        {
                            IdUsuario = transaccion.VendedorId,
                            Titulo = "Transacción marcada como PAGADA",
                            Mensaje = $"La transacción {transaccion.Codigo} fue marcada como PAGADA.",
                            Fecha = DateTime.UtcNow,
                            Leido = false
                        });
                    }

                    if (nuevoEstado == "COMPLETADO")
                    {
                        await _notificacionesRepository.Create(new Notificaciones
                        {
                            IdUsuario = transaccion.CompradorId,
                            Titulo = "Transacción completada",
                            Mensaje = $"La transacción {transaccion.Codigo} se ha completado correctamente.",
                            Fecha = DateTime.UtcNow,
                            Leido = false
                        });

                        await _notificacionesRepository.Create(new Notificaciones
                        {
                            IdUsuario = transaccion.VendedorId,
                            Titulo = "Transacción completada",
                            Mensaje = $"La transacción {transaccion.Codigo} se ha completado correctamente.",
                            Fecha = DateTime.UtcNow,
                            Leido = false
                        });
                    }
                }

                return Ok(new { mensaje = $"La transacción ha cambiado a estado: {nuevoEstado}" });
            }

            return BadRequest("No se pudo actualizar el estado. Verifica que la transacción exista.");
        }

        // POST: api/Transaccion/liberar
        [HttpPost("liberar")]
        public async Task<IActionResult> LiberarFondos([FromBody] LiberarFondosDTO dto)
        {
            var idVendedorClaim = User.FindFirst("IdUsuario")?.Value;
            if (string.IsNullOrEmpty(idVendedorClaim))
                return Unauthorized("Usuario no identificado.");

            long idVendedor = long.Parse(idVendedorClaim);

            try
            {
                var transaccion = await _transRepo.GetById(dto.IdTransaccion);
                if (transaccion == null)
                    return NotFound("Transacción no encontrada.");

                bool exito = await _transRepo.LiberarFondos(dto.IdTransaccion, idVendedor);

                if (exito)
                {
                    await _notificacionesRepository.Create(new Notificaciones
                    {
                        IdUsuario = transaccion.CompradorId,
                        Titulo = "Fondos liberados",
                        Mensaje = $"Los fondos de la transacción {transaccion.Codigo} han sido liberados por el vendedor.",
                        Fecha = DateTime.UtcNow,
                        Leido = false
                    });

                    return Ok(new { mensaje = "¡Éxito! Fondos liberados y transacción completada." });
                }

                return BadRequest("No se pudo completar la operación.");
            }
            catch (Exception ex)
            {
                return BadRequest($"ERROR AL LIBERAR FONDOS: {ex.Message}");
            }
        }

        // PUT: api/Transaccion/{id}/cancelar
        [HttpPut("{id}/cancelar")]
        public async Task<IActionResult> CancelarTransaccion(long id)
        {
            var idUsuarioClaim = User.FindFirst("IdUsuario")?.Value;
            if (string.IsNullOrEmpty(idUsuarioClaim))
                return Unauthorized("Usuario no identificado.");

            long idUsuario = long.Parse(idUsuarioClaim);

            try
            {
                bool exito = await _transRepo.CancelarTransaccion(id, idUsuario);

                if (exito)
                {
                    var transaccionCancelada = await _transRepo.GetById(id);
                    if (transaccionCancelada != null)
                    {
                        var destinatario = transaccionCancelada.CompradorId == idUsuario
                            ? transaccionCancelada.VendedorId
                            : transaccionCancelada.CompradorId;

                        await _notificacionesRepository.Create(new Notificaciones
                        {
                            IdUsuario = destinatario,
                            Titulo = "Transacción cancelada",
                            Mensaje = $"La transacción {transaccionCancelada.Codigo} ha sido cancelada.",
                            Fecha = DateTime.UtcNow,
                            Leido = false
                        });
                    }
                    return Ok(new { mensaje = "Transacción cancelada. La oferta ha vuelto a estar ACTIVA en el mercado." });
                }

                return BadRequest("No se pudo cancelar la transacción.");
            }
            catch (Exception ex)
            {
                return BadRequest($"ERROR AL CANCELAR: {ex.Message}");
            }
        }

        // GET: api/Transaccion/{id}/instrucciones
        [HttpGet("{id}/instrucciones")]
        public async Task<IActionResult> GetInstruccionesPago(long id)
        {
            var idUsuarioClaim = User.FindFirst("IdUsuario")?.Value;
            if (string.IsNullOrEmpty(idUsuarioClaim)) return Unauthorized();
            long idUsuario = long.Parse(idUsuarioClaim);

            // 1. Traemos la transacción
            var transaccion = await _transRepo.GetTransaccionConMetodoPago(id);
            if (transaccion == null) return NotFound("Transacción no encontrada.");

            if (transaccion.CompradorId != idUsuario && transaccion.VendedorId != idUsuario)
                return BadRequest("No tienes acceso a esta transacción.");

            // 2. Traemos los datos de pago reales del vendedor
            var datosVendedor = await _transRepo.GetDatosPagoVendedor(transaccion.VendedorId);
            if (datosVendedor == null)
                return BadRequest("El vendedor no tiene datos de pago registrados en el sistema.");

            var metodoPagoNombre = transaccion.IdMetodoPagoNavigation?.Nombre ?? "Desconocido";
            var oferta = transaccion.IdOfertaNavigation;
            var monedaEntregaCode = oferta?.MonedaEntregaNavigation?.Codigo;
            var monedaRecibeCode = oferta?.MonedaRecibeNavigation?.Codigo;
            decimal tasaCambio = oferta?.TasaCambio ?? 0m;
            // Calculamos montos: MontoOperacion/MontoADepositar -> en la moneda de entrega (lo que se deposita)
            // y MontoRecibe -> en la moneda que recibe el vendedor (lo que vas a recibir)
            decimal montoOperacion = transaccion.MontoOperacion ?? 0m;
            decimal montoADepositar = montoOperacion; // por defecto, lo que se deposita es el monto de la operación en la moneda de entrega
            decimal montoRecibe = montoOperacion; // por defecto igual cuando las monedas coinciden

            if (tasaCambio > 0 && !string.IsNullOrEmpty(monedaEntregaCode) && !string.IsNullOrEmpty(monedaRecibeCode))
            {
                var e = monedaEntregaCode.ToUpper();
                var r = monedaRecibeCode.ToUpper();
                if (e == r)
                {
                    montoRecibe = montoOperacion;
                }
                else if (e == "PEN" && r == "USD")
                {
                    montoRecibe = Math.Round(montoOperacion / tasaCambio, 2);
                }
                else if (e == "USD" && r == "PEN")
                {
                    montoRecibe = Math.Round(montoOperacion * tasaCambio, 2);
                }
            }

            var instrucciones = new
            {
                MetodoSeleccionado = metodoPagoNombre,
                // Monto en la moneda de la operación (entrega) — cantidad exacta que debe depositarse
                MontoOperacion = montoOperacion,
                MontoADepositar = montoADepositar,
                // Monto que vas a recibir en la moneda del vendedor
                MontoRecibe = montoRecibe,
                TasaCambio = tasaCambio,
                MonedaEntregaCode = monedaEntregaCode,
                MonedaRecibeCode = monedaRecibeCode,
                DatosDelVendedor = new
                {
                    Yape = datosVendedor.Yape,
                    Plin = datosVendedor.Plin,
                    NumeroCuenta = datosVendedor.NumeroCuenta,
                    CCI = datosVendedor.Cci
                },
                Mensaje = $"Por favor, transfiere exactamente {montoADepositar} {monedaEntregaCode} usando {metodoPagoNombre}. Una vez transferido, marca la transacción como PAGADA."
            };

            return Ok(instrucciones);
        }

        // =========================================================================
        // NUEVO: PUT para marcar como pagado y subir el voucher (El camino profesional)
        // =========================================================================

        // PUT: api/Transaccion/{id}/pagar
        [HttpPut("{id}/pagar")]
        public async Task<IActionResult> MarcarComoPagado(long id, [FromForm] IFormFile comprobante)
        {
            var idUsuarioClaim = User.FindFirst("IdUsuario")?.Value;
            if (string.IsNullOrEmpty(idUsuarioClaim)) return Unauthorized("Token inválido.");
            long idUsuario = long.Parse(idUsuarioClaim);

            // 1. Validar que realmente enviaron un archivo
            if (comprobante == null || comprobante.Length == 0)
                return BadRequest("Debes adjuntar el comprobante de pago (imagen o PDF).");

            try
            {
                // 2. Crear la carpeta "wwwroot/vouchers" si no existe
                string carpetaDestino = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "vouchers");
                if (!Directory.Exists(carpetaDestino))
                    Directory.CreateDirectory(carpetaDestino);

                // 3. Generar un nombre único para el archivo (Ej: VOUCHER_8_abc123.jpg)
                string extension = Path.GetExtension(comprobante.FileName);
                string nombreArchivo = $"VOUCHER_{id}_{Guid.NewGuid().ToString().Substring(0, 6)}{extension}";
                string rutaFisica = Path.Combine(carpetaDestino, nombreArchivo);

                // 4. Copiar el archivo físicamente al servidor
                using (var stream = new FileStream(rutaFisica, FileMode.Create))
                {
                    await comprobante.CopyToAsync(stream);
                }

                // 5. Guardar la ruta relativa en la base de datos
                string rutaParaBD = $"/vouchers/{nombreArchivo}";
                bool exito = await _transRepo.MarcarComoPagado(id, idUsuario, rutaParaBD);

                if (exito)
                {
                    var transaccionPagada = await _transRepo.GetById(id);
                    if (transaccionPagada != null)
                    {
                        await _notificacionesRepository.Create(new Notificaciones
                        {
                            IdUsuario = transaccionPagada.VendedorId,
                            Titulo = "Pago marcado como enviado",
                            Mensaje = $"El comprador ha marcado la transacción {transaccionPagada.Codigo} como PAGADA y subió un comprobante.",
                            Fecha = DateTime.UtcNow,
                            Leido = false
                        });
                    }
                    return Ok(new { mensaje = "Comprobante subido y transacción marcada como PAGADA exitosamente.", ruta = rutaParaBD });
                }

                return BadRequest("No se pudo actualizar el estado de la transacción.");
            }
            catch (Exception ex)
            {
                return BadRequest($"ERROR AL PROCESAR EL PAGO: {ex.Message}");
            }
        }

        [HttpPost("pagar-con-wallet/{idTransaccion}")]
        public async Task<IActionResult> PagarConWallet(long idTransaccion)
        {
            var idUsuarioClaim = User.FindFirst("IdUsuario")?.Value;
            if (string.IsNullOrEmpty(idUsuarioClaim))
                return Unauthorized("Usuario no identificado.");

            long idUsuario = long.Parse(idUsuarioClaim);

            try
            {
                bool exito = await _transRepo.PagarConWallet(idTransaccion, idUsuario);

                if (exito)
                {
                    var transaccionWallet = await _transRepo.GetById(idTransaccion);
                    if (transaccionWallet != null)
                    {
                        await _notificacionesRepository.Create(new Notificaciones
                        {
                            IdUsuario = transaccionWallet.VendedorId,
                            Titulo = "Pago con wallet realizado",
                            Mensaje = $"El comprador pagó con wallet la transacción {transaccionWallet.Codigo}.",
                            Fecha = DateTime.UtcNow,
                            Leido = false
                        });
                    }
                    return Ok(new { mensaje = "Pago con wallet exitoso. Transacción completada." });
                }

                return BadRequest("No se pudo completar el pago con wallet.");
            }
            catch (Exception ex)
            {
                return BadRequest($"ERROR AL PAGAR CON WALLET: {ex.Message}");
            }
        }
    }
}