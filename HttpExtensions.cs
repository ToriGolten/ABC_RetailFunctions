using Microsoft.Azure.Functions.Worker.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ABCRetail_Functions
{
    public static class HttpExtensions
    {
        private static readonly JsonSerializerOptions _json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        public static async Task<HttpResponseData> JsonAsync<T>(this HttpRequestData req, T payload, HttpStatusCode code = HttpStatusCode.OK)
        {
            var res = req.CreateResponse(code);
            await res.WriteStringAsync(JsonSerializer.Serialize(payload, _json));
            res.Headers.Add("Content-Type", "application/json");
            return res;
        }

        public static async Task<HttpResponseData> ProblemAsync(this HttpRequestData req, string message, HttpStatusCode code = HttpStatusCode.BadRequest)
        {
            var res = req.CreateResponse(code);
            await res.WriteStringAsync(JsonSerializer.Serialize(new { error = message }, _json));
            res.Headers.Add("Content-Type", "application/json");
            return res;
        }
    }
}