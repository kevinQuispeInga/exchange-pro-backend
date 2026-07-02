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
            var transaccionesCompletadas = await _context.Transacciones.CountAsync(t => t.Estado == "COMPLETADA");
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
                return new ChatbotResponseDTO { Respuesta = "Error: API Key de Gemini no configurada." };
            }

            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.5-flash:generateContent?key={apiKey}";

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
    }
}
