using Microsoft.EntityFrameworkCore;
using UESAN.ExchangePro.CORE.Core.Entities;
using UESAN.ExchangePro.CORE.Infrastructure.Data;

public class DatosPagoRepository : IDatosPagoRepository
{
    private readonly ExchangeProDbContext _context;
    public DatosPagoRepository(ExchangeProDbContext context) => _context = context;

    public async Task<bool> Insert(DatosPagoUsuario datoPago)
    {
        await _context.DatosPagoUsuario.AddAsync(datoPago);
        return await _context.SaveChangesAsync() > 0;
    }

    public async Task<IEnumerable<DatosPagoUsuario>> GetByUsuario(long idUsuario)
    {
        return await _context.DatosPagoUsuario
            .Where(d => d.IdUsuario == idUsuario)
            .Include(d => d.IdBancoNavigation)
            .ToListAsync();
    }

    public async Task<bool> Update(DatosPagoUsuario datoPago)
    {
        _context.DatosPagoUsuario.Update(datoPago);
        return await _context.SaveChangesAsync() > 0;
    }
}