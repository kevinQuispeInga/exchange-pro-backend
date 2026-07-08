using Microsoft.EntityFrameworkCore;
using UESAN.ExchangePro.CORE.Core.Interfaces;
using UESAN.ExchangePro.CORE.Infrastructure.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace UESAN.ExchangePro.CORE.Core.Services
{
    public class AdminService : IAdminService
    {
        private readonly ExchangeProDbContext _context;
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _config;

        public AdminService(ExchangeProDbContext context, HttpClient httpClient, IConfiguration config)
        {
            _context = context;
            _httpClient = httpClient;
            _config = config;
        }

        public async Task<AdminEstadisticasDTO> GetEstadisticas()
        {
            var totalUsuarios = await _context.Usuarios.CountAsync();
            var ofertasActivas = await _context.Ofertas.CountAsync(o => o.Estado == "ACTIVA");
            var transaccionesCompletadas = await _context.Transacciones.CountAsync(t => t.Estado == "COMPLETADO" || t.Estado == "COMPLETADA");
            var disputasPendientes = await _context.Disputas.CountAsync(d => d.Estado == "PENDIENTE" || d.Estado == "EN_REVISION");
            var feedbackPendientes = await _context.Set<UESAN.ExchangePro.CORE.Core.Entities.Feedback>()
                .CountAsync(f => f.Estado == "PENDIENTE");

            return new AdminEstadisticasDTO
            {
                TotalUsuarios = totalUsuarios,
                OfertasActivas = ofertasActivas,
                TransaccionesCompletadas = transaccionesCompletadas,
                DisputasPendientes = disputasPendientes,
                FeedbackPendientes = feedbackPendientes
            };
        }

        public async Task<ChatbotResponseDTO> ChatbotResponder(string mensaje)
        {
            var apiKey = _config["Gemini:ApiKey"];
            if (string.IsNullOrEmpty(apiKey))
            {
                apiKey = (_config["Gemini:Parte1"] ?? "") + (_config["Gemini:Parte2"] ?? "");
            }
            if (string.IsNullOrEmpty(apiKey))
            {
                return new ChatbotResponseDTO { Respuesta = "Error: API Key de Gemini no configurada." };
            }

            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={apiKey}";

            var systemInstruction = "Eres el asistente oficial de ExchangePro, una plataforma P2P de intercambio de divisas (Soles PEN, Dólares USD, etc.). Tu labor es responder de forma amable, empática y concisa dudas sobre cómo comprar, cómo vender, publicar ofertas, depósitos, retiros y resolución de disputas. Si te preguntan cosas ajenas a ExchangePro o las finanzas P2P, responde de manera cortés indicando que solo puedes asistir en temas relacionados a la plataforma.";

            var requestBody = new
            {
                systemInstruction = new { parts = new[] { new { text = systemInstruction } } },
                contents = new[] { new { parts = new[] { new { text = mensaje } } } }
            };

            var jsonPayload = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient.PostAsync(url, content);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseJson);
                
                var textResponse = doc.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString();

                return new ChatbotResponseDTO { Respuesta = textResponse ?? "No obtuve una respuesta clara." };
            }
            catch (Exception)
            {
                return new ChatbotResponseDTO { Respuesta = "Lo siento, en este momento tengo problemas de conexión con mi motor de inteligencia artificial. Por favor, vuelve a intentarlo en unos instantes." };
            }
        }

        public async Task<List<int>> GetHistorial(string metrica, int? anio, int? mes, int? semana)
        {
            var hoy = DateTime.UtcNow;
            int targetAnio = anio ?? hoy.Year;
            var counts = new List<int>();

            // Caso 1: Solo Año seleccionado -> retornar 12 meses
            if (mes == null)
            {
                for (int m = 1; m <= 12; m++)
                {
                    var fechaInicio = new DateTime(targetAnio, m, 1);
                    var fechaFin = fechaInicio.AddMonths(1);

                    int count = await GetCountForRange(metrica, fechaInicio, fechaFin);
                    counts.Add(count);
                }
            }
            // Caso 2: Año y Mes seleccionados, pero sin semana específica -> retornar las semanas de ese mes (4 o 5 semanas)
            else if (semana == null)
            {
                int targetMes = mes.Value;
                int maxDays = DateTime.DaysInMonth(targetAnio, targetMes);

                var weekRanges = new List<(DateTime start, DateTime end)>
                {
                    (new DateTime(targetAnio, targetMes, 1), new DateTime(targetAnio, targetMes, 8)),
                    (new DateTime(targetAnio, targetMes, 8), new DateTime(targetAnio, targetMes, 15)),
                    (new DateTime(targetAnio, targetMes, 15), new DateTime(targetAnio, targetMes, 22)),
                    (new DateTime(targetAnio, targetMes, 22), new DateTime(targetAnio, targetMes, 29)),
                };
                if (maxDays >= 29)
                {
                    weekRanges.Add((new DateTime(targetAnio, targetMes, 29), new DateTime(targetAnio, targetMes, maxDays).AddDays(1)));
                }

                foreach (var range in weekRanges)
                {
                    int count = await GetCountForRange(metrica, range.start, range.end);
                    counts.Add(count);
                }
            }
            // Caso 3: Semana específica seleccionada -> retornar los 7 días de esa semana (o los días restantes si es la semana 5)
            else
            {
                int targetMes = mes.Value;
                int targetSemana = semana.Value;
                int maxDays = DateTime.DaysInMonth(targetAnio, targetMes);

                int startDay = (targetSemana - 1) * 7 + 1;
                int endDay = startDay + 7;

                if (startDay > maxDays)
                {
                    return counts;
                }

                if (endDay > maxDays + 1)
                {
                    endDay = maxDays + 1;
                }

                for (int d = startDay; d < endDay; d++)
                {
                    var fechaInicio = new DateTime(targetAnio, targetMes, d);
                    var fechaFin = fechaInicio.AddDays(1);

                    int count = await GetCountForRange(metrica, fechaInicio, fechaFin);
                    counts.Add(count);
                }
            }

            return counts;
        }

        private async Task<int> GetCountForRange(string metrica, DateTime start, DateTime end)
        {
            switch (metrica)
            {
                case "usuarios":
                    return await _context.Usuarios.CountAsync(u => u.FechaRegistro >= start && u.FechaRegistro < end);
                case "ofertas":
                    return await _context.Ofertas.CountAsync(o => o.FechaPublicacion >= start && o.FechaPublicacion < end);
                case "transacciones":
                    return await _context.Transacciones.CountAsync(t => t.FechaInicio >= start && t.FechaInicio < end);
                case "disputas":
                    return await _context.Disputas.CountAsync(d => d.FechaCreacion >= start && d.FechaCreacion < end);
                case "feedback":
                    return await _context.Feedback.CountAsync(f => f.FechaCreacion >= start && f.FechaCreacion < end);
                default:
                    return 0;
            }
        }
    }
}
