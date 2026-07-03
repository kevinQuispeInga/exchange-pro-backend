using System;

namespace UESAN.ExchangePro.CORE.Core.DTOs
{
    public class TransaccionResponseDTO
    {
        public long IdTransaccion { get; set; }
        public long IdOferta { get; set; }
        public long CompradorId { get; set; }
        public long VendedorId { get; set; }
        public decimal? MontoOperacion { get; set; }
        public string? Estado { get; set; }
        public DateTime? FechaInicio { get; set; }
        public string? Codigo { get; set; }
        public string? RutaComprobante { get; set; }
        public string? MonedaEntregaCode { get; set; }
        public string? MonedaRecibeCode { get; set; }
        public string? CompradorNombre { get; set; }
        public string? VendedorNombre { get; set; }
        public decimal? TotalPagar { get; set; }
    }
}