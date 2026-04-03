using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Snacks.Services
{
    public class ClusterAuthFilter : IActionFilter
    {
        private readonly ClusterService _clusterService;

        public ClusterAuthFilter(ClusterService clusterService)
        {
            _clusterService = clusterService;
        }

        public void OnActionExecuting(ActionExecutingContext context)
        {
            var config = _clusterService.GetConfig();
            if (string.IsNullOrEmpty(config.SharedSecret))
            {
                context.Result = new UnauthorizedObjectResult("Cluster secret not configured");
                return;
            }

            var providedSecret = context.HttpContext.Request.Headers["X-Snacks-Secret"].FirstOrDefault();
            if (string.IsNullOrEmpty(providedSecret))
            {
                context.Result = new UnauthorizedObjectResult("Missing X-Snacks-Secret header");
                return;
            }

            // Constant-time comparison to prevent timing attacks
            if (!CryptographicEquals(config.SharedSecret, providedSecret))
            {
                context.Result = new UnauthorizedObjectResult("Invalid cluster secret");
                return;
            }
        }

        public void OnActionExecuted(ActionExecutedContext context) { }

        private static bool CryptographicEquals(string a, string b)
        {
            // HMAC both values so the comparison is always fixed-length (32 bytes)
            // regardless of input lengths — prevents length leakage via timing
            var key = System.Text.Encoding.UTF8.GetBytes("snacks-auth-compare");
            var hashA = System.Security.Cryptography.HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes(a));
            var hashB = System.Security.Cryptography.HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes(b));
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(hashA, hashB);
        }
    }
}
