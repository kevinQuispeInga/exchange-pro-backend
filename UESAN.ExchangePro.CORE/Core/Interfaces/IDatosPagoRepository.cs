using UESAN.ExchangePro.CORE.Core.Entities;

public interface IDatosPagoRepository
{
    Task<bool> Insert(DatosPagoUsuario datoPago);
    Task<IEnumerable<DatosPagoUsuario>> GetByUsuario(long idUsuario);
    Task<bool> Update(DatosPagoUsuario datoPago);
}