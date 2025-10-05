using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ABCRetail_Functions
{
    internal class Models
    {
        public record EnqueueRequest(object? Message, int? VisibilityTimeoutSeconds, int? TimeToLiveSeconds);
        public record UpsertEntityDto(string PartitionKey, string RowKey, Dictionary<string, object>? Properties);
    }
}
