using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using UESAN.ExchangePro.CORE.Core.Entities;
using UESAN.ExchangePro.CORE.Core.Interfaces;
using UESAN.ExchangePro.CORE.Infrastructure.Data;

namespace UESAN.ExchangePro.Infrastructure.Repositories
{
    public class RecargaRepository : IRecargaRepository
    {
        private readonly ExchangeProDbContext _context;

        public RecargaRepository(ExchangeProDbContext context)
        {
            _context = context;
        }

        public async Task<bool> Insert(Recargas recarga)
        {
            await _context.Recargas.AddAsync(recarga);
            return await _context.SaveChangesAsync() > 0;
        }

        public async Task<bool> ExisteReferencia(string numeroReferencia)
        {
            return await _context.Recargas.AnyAsync(r => r.NumeroReferencia == numeroReferencia);
        }
    }
}