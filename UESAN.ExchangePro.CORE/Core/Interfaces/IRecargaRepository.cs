using System.Threading.Tasks;
using UESAN.ExchangePro.CORE.Core.Entities;

namespace UESAN.ExchangePro.CORE.Core.Interfaces
{
    public interface IRecargaRepository
    {
        Task<bool> Insert(Recargas recarga);
        Task<bool> ExisteReferencia(string numeroReferencia);
    }
}