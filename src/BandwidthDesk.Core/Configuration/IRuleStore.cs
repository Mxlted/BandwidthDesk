using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BandwidthDesk.Core.Models;

namespace BandwidthDesk.Core.Configuration;

public interface IRuleStore
{
    Task<IReadOnlyList<BandwidthRule>> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(IEnumerable<BandwidthRule> rules, CancellationToken ct = default);
}
