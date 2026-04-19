using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Snacks.Services;

/// <summary>
///     ASP.NET MVC action filter that authenticates inter-node cluster requests.
///     Applied to all endpoints on <see cref="Controllers.ClusterController"/>.
///     Validates the <c>X-Snacks-Secret</c> header using constant-time comparison
///     to prevent timing-based secret enumeration.
/// </summary>
public sealed class ClusterAuthFilter : IActionFilter
{
    private readonly ClusterService _clusterService;

    /// <summary> Creates a new filter using the cluster service to retrieve the configured secret. </summary>
    /// <param name="clusterService">The cluster service that holds the shared secret.</param>
    public ClusterAuthFilter(ClusterService clusterService)
    {
        ArgumentNullException.ThrowIfNull(clusterService);
        _clusterService = clusterService;
    }

    /******************************************************************
     *  IActionFilter
     ******************************************************************/

    /// <summary>
    ///     Validates the <c>X-Snacks-Secret</c> header before the action executes.
    ///     Short-circuits the request with 401 Unauthorized if the header is missing or invalid.
    /// </summary>
    /// <param name="context">The action execution context.</param>
    public void OnActionExecuting(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var config = _clusterService.GetConfig();
        if (string.IsNullOrEmpty(config.SharedSecret))
        {
            context.Result = new UnauthorizedObjectResult("Cluster secret not configured");
            return;
        }

        var providedHeader = context.HttpContext.Request.Headers["X-Snacks-Secret"].FirstOrDefault();
        if (string.IsNullOrEmpty(providedHeader))
        {
            context.Result = new UnauthorizedObjectResult("Missing X-Snacks-Secret header");
            return;
        }

        if (!TryDecodeSecretHeader(providedHeader, out var providedSecret))
        {
            context.Result = new UnauthorizedObjectResult("Invalid cluster secret");
            return;
        }

        if (!CryptographicEquals(config.SharedSecret, providedSecret))
        {
            context.Result = new UnauthorizedObjectResult("Invalid cluster secret");
            return;
        }
    }

    /// <summary>
    ///     Encodes the shared secret as Base64(UTF-8) so it can be sent in an HTTP header.
    ///     HTTP headers must be ASCII; secrets may contain any Unicode character.
    /// </summary>
    /// <param name="secret">The raw shared secret.</param>
    /// <returns>An ASCII-safe Base64 representation of the secret's UTF-8 bytes.</returns>
    public static string EncodeSecretForHeader(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(secret));
    }

    private static bool TryDecodeSecretHeader(string headerValue, out string decoded)
    {
        try
        {
            decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(headerValue));
            return true;
        }
        catch (FormatException)
        {
            decoded = "";
            return false;
        }
    }

    /// <summary> No-op post-execution hook required by <see cref="IActionFilter"/>. </summary>
    /// <param name="context">The action executed context.</param>
    public void OnActionExecuted(ActionExecutedContext context)
    {
    }

    /******************************************************************
     *  Helpers
     ******************************************************************/

    /// <summary>
    ///     Compares two strings in constant time using HMAC-SHA256 to prevent timing attacks.
    ///     Both inputs are hashed to a fixed-length 32-byte value before comparison,
    ///     eliminating length leakage.
    /// </summary>
    /// <param name="a">The expected secret.</param>
    /// <param name="b">The provided secret from the request header.</param>
    /// <returns><see langword="true"/> if the strings are equal; otherwise <see langword="false"/>.</returns>
    private static bool CryptographicEquals(string a, string b)
    {
        // Hashing both values to a fixed length eliminates timing differences
        // that would otherwise reveal the length of the configured secret.
        var key    = System.Text.Encoding.UTF8.GetBytes("snacks-auth-compare");
        var hashA  = System.Security.Cryptography.HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes(a));
        var hashB  = System.Security.Cryptography.HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes(b));
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(hashA, hashB);
    }
}
