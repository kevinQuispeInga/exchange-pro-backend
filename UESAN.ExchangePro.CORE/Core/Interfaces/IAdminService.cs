using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UESAN.ExchangePro.CORE.Core.Interfaces
{
    public class AdminEstadisticasDTO
    {
        public int TotalUsuarios { get; set; }
        public int OfertasActivas { get; set; }
        public int TransaccionesCompletadas { get; set; }
        public int DisputasPendientes { get; set; }
        public int FeedbackPendientes { get; set; }
    }

    public class ChatbotResponseDTO
    {
        public string Respuesta { get; set; } = null!;
    }

    public interface IAdminService
    {
        Task<AdminEstadisticasDTO> GetEstadisticas();
        Task<ChatbotResponseDTO> ChatbotResponder(string mensaje);
        Task<List<int>> GetHistorial(string metrica, int? anio, int? mes, int? semana);
    }
}
