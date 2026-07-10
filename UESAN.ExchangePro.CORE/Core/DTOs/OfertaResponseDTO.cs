namespace UESAN.ExchangePro.CORE.Core.DTOs
{
    public class OfertaResponseDTO
    {

        public long IdOferta { get; set; }
        public long IdUsuario { get; set; }
        public string NombreUsuario { get; set; } = null!;
        public decimal MontoOfertado { get; set; }
        public decimal MontoMinimo { get; set; }
        public decimal TasaCambio { get; set; }
        public string TipoOperacion { get; set; } = null!;
        public string Estado { get; set; } = null!;
        public int MonedaEntrega { get; set; }
        public int MonedaRecibe { get; set; }
        public string? MonedaEntregaCode { get; set; }
        public string? MonedaRecibeCode { get; set; }
        public System.DateTime? FechaPublicacion { get; set; }
    }
}