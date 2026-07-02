using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using UESAN.ExchangePro.CORE.Core.Interfaces;
using UESAN.ExchangePro.CORE.Core.Services;
using UESAN.ExchangePro.CORE.Infrastructure.Data;
using UESAN.ExchangePro.CORE.Infrastructure.Repositories;
using UESAN.ExchangePro.CORE.Infrastructure.Services;
using UESAN.ExchangePro.Infrastructure.Repositories;

var builder = WebApplication.CreateBuilder(args);

// =========================================================================
// CORREGIDO: CONFIGURACIÓN DE CORS PARA QUASAR (Puerto 9000)
// =========================================================================
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowQuasar", policy =>
    {
        policy.AllowAnyOrigin()                   // Acepta desde cualquier origen (ngrok, localhost, etc.)
              .AllowAnyMethod()                     // Permite POST, GET, PUT, DELETE
              .AllowAnyHeader();                    // Permite Content-Type, Authorization (JWT)
    });
});

// 1. Configuración de Base de Datos
builder.Services.AddDbContext<ExchangeProDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// 2. Registro de Repositorios y Servicios (Inyección de Dependencias)
builder.Services.AddTransient<IUsuarioRepository, UsuarioRepository>();
builder.Services.AddTransient<IWalletRepository, WalletRepository>();
builder.Services.AddTransient<IAuthService, AuthService>();
builder.Services.AddTransient<IDatosPagoRepository, DatosPagoRepository>();
builder.Services.AddTransient<IOfertaRepository, OfertaRepository>();
builder.Services.AddTransient<ITransaccionRepository, TransaccionRepository>();
builder.Services.AddTransient<IDisputaRepository, DisputaRepository>();
builder.Services.AddTransient<IFeedbackRepository, FeedbackRepository>();
builder.Services.AddTransient<ICalificacionRepository, CalificacionRepository>();
builder.Services.AddTransient<IMovimientoWalletRepository, MovimientoWalletRepository>();
builder.Services.AddTransient<IRetiroRepository, RetiroRepository>();
builder.Services.AddHttpClient<IAdminService, AdminService>();
builder.Services.AddTransient<IEmailService, EmailService>();
builder.Services.AddTransient<INotificacionesRepository, NotificacionesRepository>();
builder.Services.AddTransient<IRecargaRepository, RecargaRepository>();
builder.Services.AddHttpClient<ITipoCambioService, TipoCambioService>();
builder.Services.AddMemoryCache();

// 3. Configuración de Seguridad JWT
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT Key is missing in appsettings.json")))
        };
    });

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapibuilder
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

//app.UseHttpsRedirection();

// =========================================================================
// CORREGIDO: ACTIVAR MIDDLEWARE DE RUTAS, CORS y AUTENTICACIÓN
// =========================================================================
app.UseRouting();
app.UseCors("AllowQuasar");

// MUY IMPORTANTE: UseAuthentication siempre debe ir ANTES de UseAuthorization
app.UseAuthentication();
app.UseAuthorization();

app.UseStaticFiles();

app.MapControllers();

app.Run();